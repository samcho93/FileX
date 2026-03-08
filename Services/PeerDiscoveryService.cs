using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FileX.Models;

namespace FileX.Services;

public class PeerDiscoveryService
{
    private readonly int _port;
    private readonly int _discoveryPort;
    private readonly string _machineName = Environment.MachineName;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];
    private readonly int _intervalSeconds;
    private readonly int _timeoutSeconds;
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();

    // Multicast group for peer discovery (organization-local scope)
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.45.88");

    public event Action<PeerInfo>? OnPeerDiscovered;
    public event Action<string>? OnPeerLost;
    public event Action<string>? OnError;
    public event Action<string>? OnStatusChanged;

    public PeerDiscoveryService(int port, int discoveryPort, int intervalSeconds, int timeoutSeconds)
    {
        _port = port;
        _discoveryPort = discoveryPort;
        _intervalSeconds = intervalSeconds;
        _timeoutSeconds = timeoutSeconds;
    }

    public string MachineName => _machineName;

    public List<PeerInfo> GetPeers() => _peers.Values.ToList();
    public PeerInfo? GetPeer(string peerId) => _peers.GetValueOrDefault(peerId);

    /// <summary>
    /// Register a peer that connected to us (called from the web server endpoint).
    /// </summary>
    public void RegisterIncomingPeer(string ip, int peerPort, string machineName)
    {
        var peerId = $"{ip}:{peerPort}";
        var isNew = !_peers.ContainsKey(peerId);
        _peers[peerId] = new PeerInfo
        {
            Id = peerId,
            MachineName = machineName,
            Address = $"http://{ip}:{peerPort}",
            LastSeen = DateTime.UtcNow,
            IsManual = true
        };
        if (isNew)
        {
            OnStatusChanged?.Invoke($"Peer connected: {machineName} ({ip})");
            OnPeerDiscovered?.Invoke(_peers[peerId]);
        }
    }

    /// <summary>
    /// Manually add a peer by IP address (and optional port).
    /// Returns the PeerInfo if the remote peer responds, null otherwise.
    /// </summary>
    public async Task<PeerInfo?> AddManualPeerAsync(string addressInput)
    {
        try
        {
            // Parse input: could be "192.168.1.5" or "192.168.1.5:5000"
            string ip;
            int peerPort;
            if (addressInput.Contains(':'))
            {
                var parts = addressInput.Split(':');
                ip = parts[0];
                peerPort = int.Parse(parts[1]);
            }
            else
            {
                ip = addressInput;
                peerPort = _port; // assume same port
            }

            var address = $"http://{ip}:{peerPort}";
            var peerId = $"{ip}:{peerPort}";

            // Check if already connected
            if (_peers.ContainsKey(peerId))
            {
                var existing = _peers[peerId];
                existing.LastSeen = DateTime.UtcNow;
                OnPeerDiscovered?.Invoke(existing);
                return existing;
            }

            // Try to connect to the peer
            OnStatusChanged?.Invoke($"Connecting to {address}...");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // First try /api/peer/info to get machine name
            var machineName = ip;
            try
            {
                var infoResp = await http.GetAsync($"{address}/api/peer/info");
                if (infoResp.IsSuccessStatusCode)
                {
                    var json = await infoResp.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    machineName = doc.RootElement.GetProperty("machineName").GetString() ?? ip;
                }
            }
            catch { /* fallback to IP */ }

            // Verify connectivity by fetching drives
            var resp = await http.GetAsync($"{address}/api/peer/drives");
            resp.EnsureSuccessStatusCode();

            // Tell the remote peer about us so it can see us too
            try
            {
                var myInfo = JsonSerializer.Serialize(new { machineName = _machineName, port = _port });
                await http.PostAsync($"{address}/api/peer/connect",
                    new StringContent(myInfo, Encoding.UTF8, "application/json"));
            }
            catch { /* best effort — remote will still work even if this fails */ }

            var peer = new PeerInfo
            {
                Id = peerId,
                MachineName = machineName,
                Address = address,
                LastSeen = DateTime.UtcNow,
                IsManual = true
            };

            _peers[peerId] = peer;
            OnPeerDiscovered?.Invoke(peer);
            OnStatusChanged?.Invoke($"Connected to {machineName} ({ip})");
            return peer;
        }
        catch (HttpRequestException ex)
        {
            OnError?.Invoke($"Connection refused: {ex.Message} — Check if FileX is running on the remote PC and firewall allows port {_port}");
            return null;
        }
        catch (TaskCanceledException)
        {
            OnError?.Invoke($"Connection timed out to {addressInput} — Check network and firewall");
            return null;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to connect: {ex.Message}");
            return null;
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        OnStatusChanged?.Invoke("Starting peer discovery...");
        try
        {
            await Task.WhenAll(
                TcpScanAsync(ct),       // Primary: TCP subnet scan (most reliable)
                SendAsync(ct),          // Secondary: UDP multicast + broadcast
                ListenAsync(ct),        // Secondary: UDP listener
                CleanupAsync(ct));
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Discovery service stopped: {ex.Message}");
        }
    }

    /// <summary>
    /// TCP-based peer discovery: scans all IPs in local subnets for FileX instances.
    /// This is the most reliable method since TCP works on any network where manual connect works.
    /// </summary>
    private async Task TcpScanAsync(CancellationToken ct)
    {
        // Wait for web server to be ready before scanning
        await Task.Delay(3000, ct);

        OnStatusChanged?.Invoke("Scanning local network for peers...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var localAddresses = GetLocalAddressesWithMask();
                if (localAddresses.Count == 0)
                {
                    OnError?.Invoke("No network interfaces found for scanning");
                    await Task.Delay(30_000, ct);
                    continue;
                }

                foreach (var (localIp, mask) in localAddresses)
                {
                    if (ct.IsCancellationRequested) break;

                    var candidates = GetSubnetHosts(localIp, mask);
                    if (candidates.Count == 0) continue;

                    OnStatusChanged?.Invoke($"Scanning {candidates.Count} hosts on {localIp}...");

                    // Scan in parallel with limited concurrency
                    var semaphore = new SemaphoreSlim(30); // 30 concurrent connections
                    var tasks = candidates.Select(async ip =>
                    {
                        await semaphore.WaitAsync(ct);
                        try
                        {
                            await TryConnectPeer(ip, _port, ct);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    await Task.WhenAll(tasks);
                }

                var peerCount = _peers.Count;
                OnStatusChanged?.Invoke(peerCount > 0
                    ? $"Found {peerCount} peer(s). Next scan in {_intervalSeconds * 3}s..."
                    : $"No peers found. Next scan in {_intervalSeconds * 3}s...");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                OnError?.Invoke($"Scan error: {ex.Message}");
            }

            // Wait before next scan (longer interval than UDP since scanning is heavier)
            try { await Task.Delay(_intervalSeconds * 3000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Try to connect to a potential peer via TCP HTTP.
    /// </summary>
    private async Task TryConnectPeer(string ip, int port, CancellationToken ct)
    {
        var peerId = $"{ip}:{port}";

        // Skip if already known
        if (_peers.ContainsKey(peerId))
        {
            _peers[peerId].LastSeen = DateTime.UtcNow;
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            var address = $"http://{ip}:{port}";

            // Quick check: try /api/peer/info
            var resp = await http.GetAsync($"{address}/api/peer/info", ct);
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var machineName = doc.RootElement.GetProperty("machineName").GetString() ?? ip;

            // It's a FileX instance! Register it.
            var peer = new PeerInfo
            {
                Id = peerId,
                MachineName = machineName,
                Address = address,
                LastSeen = DateTime.UtcNow
            };

            var isNew = !_peers.ContainsKey(peerId);
            _peers[peerId] = peer;

            if (isNew)
            {
                OnStatusChanged?.Invoke($"Discovered peer: {machineName} ({ip})");
                OnPeerDiscovered?.Invoke(peer);

                // Tell the remote peer about us (bidirectional registration)
                try
                {
                    var myInfo = JsonSerializer.Serialize(new { machineName = _machineName, port = _port });
                    await http.PostAsync($"{address}/api/peer/connect",
                        new StringContent(myInfo, Encoding.UTF8, "application/json"), ct);
                }
                catch { }
            }
        }
        catch
        {
            // Connection failed — not a FileX peer or unreachable
        }
    }

    /// <summary>
    /// Get all local IPv4 addresses with their subnet masks.
    /// </summary>
    private static List<(IPAddress Address, IPAddress Mask)> GetLocalAddressesWithMask()
    {
        var result = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    result.Add((addr.Address, addr.IPv4Mask));
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Generate all host IPs in a subnet (excluding network, broadcast, and self).
    /// For /24 subnets: generates up to 253 IPs.
    /// For larger subnets: limits to first 254 hosts to avoid excessive scanning.
    /// </summary>
    private static List<string> GetSubnetHosts(IPAddress address, IPAddress mask)
    {
        var hosts = new List<string>();
        var ipBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        // Calculate network address and host count
        var networkBytes = new byte[4];
        var broadcastBytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
            broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        }

        var networkInt = BitConverter.ToUInt32(networkBytes.Reverse().ToArray(), 0);
        var broadcastInt = BitConverter.ToUInt32(broadcastBytes.Reverse().ToArray(), 0);
        var selfInt = BitConverter.ToUInt32(ipBytes.Reverse().ToArray(), 0);

        // Limit scan range to 254 hosts max
        var hostCount = broadcastInt - networkInt - 1;
        if (hostCount > 254) hostCount = 254;

        for (uint i = 1; i <= hostCount; i++)
        {
            var hostInt = networkInt + i;
            if (hostInt == selfInt) continue; // skip self
            if (hostInt == broadcastInt) continue; // skip broadcast

            var bytes = BitConverter.GetBytes(hostInt).Reverse().ToArray();
            hosts.Add(new IPAddress(bytes).ToString());
        }

        return hosts;
    }

    // ===== UDP Discovery (secondary, kept as fallback) =====

    /// <summary>
    /// Send discovery messages via both UDP multicast and broadcast.
    /// </summary>
    private async Task SendAsync(CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

            var msg = JsonSerializer.Serialize(new { instanceId = _instanceId, machineName = _machineName, port = _port });
            var data = Encoding.UTF8.GetBytes(msg);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Multicast
                    await client.SendAsync(data, data.Length, new IPEndPoint(MulticastGroup, _discoveryPort));
                }
                catch { }

                // Subnet broadcast
                foreach (var addr in GetSubnetBroadcastAddresses())
                {
                    try { await client.SendAsync(data, data.Length, new IPEndPoint(addr, _discoveryPort)); }
                    catch { }
                }

                // Limited broadcast
                try { await client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, _discoveryPort)); }
                catch { }

                try { await Task.Delay(_intervalSeconds * 1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch { }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            using var listener = new UdpClient();
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));

            try { listener.JoinMulticastGroup(MulticastGroup); }
            catch
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    if (!ni.SupportsMulticast) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        try { listener.JoinMulticastGroup(MulticastGroup, addr.Address); } catch { }
                    }
                }
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await listener.ReceiveAsync(ct);
                    ProcessDiscoveryMessage(result);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
                }
            }
        }
        catch { }
    }

    private void ProcessDiscoveryMessage(UdpReceiveResult result)
    {
        try
        {
            var json = Encoding.UTF8.GetString(result.Buffer);
            var d = JsonSerializer.Deserialize<JsonElement>(json);
            var instId = d.GetProperty("instanceId").GetString() ?? "";
            if (instId == _instanceId) return;

            var name = d.GetProperty("machineName").GetString() ?? "";
            var peerPort = d.GetProperty("port").GetInt32();
            var ip = result.RemoteEndPoint.Address.ToString();
            if (ip.StartsWith("::ffff:")) ip = ip[7..];

            var peerId = $"{ip}:{peerPort}";
            var isNew = !_peers.ContainsKey(peerId);
            var peerInfo = new PeerInfo
            {
                Id = peerId,
                MachineName = name,
                Address = $"http://{ip}:{peerPort}",
                LastSeen = DateTime.UtcNow
            };
            _peers[peerId] = peerInfo;
            if (isNew)
            {
                OnStatusChanged?.Invoke($"Discovered peer: {name} ({ip})");
                OnPeerDiscovered?.Invoke(peerInfo);
            }
            else
            {
                _peers[peerId].LastSeen = DateTime.UtcNow;
            }
        }
        catch { }
    }

    private static List<IPAddress> GetSubnetBroadcastAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ipBytes = addr.Address.GetAddressBytes();
                    var maskBytes = addr.IPv4Mask.GetAddressBytes();
                    var broadcastBytes = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                    result.Add(new IPAddress(broadcastBytes));
                }
            }
        }
        catch { }
        return result;
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(10_000, ct); }
            catch (OperationCanceledException) { break; }

            foreach (var p in _peers.Where(x =>
                !x.Value.IsManual &&
                (DateTime.UtcNow - x.Value.LastSeen).TotalSeconds > _timeoutSeconds).ToList())
            {
                if (_peers.TryRemove(p.Key, out _))
                    OnPeerLost?.Invoke(p.Key);
            }
        }
    }
}
