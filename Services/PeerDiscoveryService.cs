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
        OnStatusChanged?.Invoke("Starting peer discovery (multicast + broadcast)...");
        try
        {
            await Task.WhenAll(
                SendAsync(ct),
                ListenAsync(ct),
                CleanupAsync(ct));
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Discovery service stopped: {ex.Message}");
        }
    }

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
            // Multicast TTL = 2 (stay within local network)
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

            var msg = JsonSerializer.Serialize(new { instanceId = _instanceId, machineName = _machineName, port = _port });
            var data = Encoding.UTF8.GetBytes(msg);

            OnStatusChanged?.Invoke($"Sending discovery on multicast {MulticastGroup} + broadcast, port {_discoveryPort}");

            while (!ct.IsCancellationRequested)
            {
                // 1. Send to multicast group
                try
                {
                    await client.SendAsync(data, data.Length, new IPEndPoint(MulticastGroup, _discoveryPort));
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Multicast send error: {ex.Message}");
                }

                // 2. Send to subnet broadcast addresses
                var broadcastAddrs = GetSubnetBroadcastAddresses();
                foreach (var addr in broadcastAddrs)
                {
                    try
                    {
                        await client.SendAsync(data, data.Length, new IPEndPoint(addr, _discoveryPort));
                    }
                    catch { /* skip failed interface */ }
                }

                // 3. Send to limited broadcast as fallback
                try
                {
                    await client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, _discoveryPort));
                }
                catch { }

                try { await Task.Delay(_intervalSeconds * 1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (SocketException ex)
        {
            OnError?.Invoke($"Cannot start discovery sender: {ex.Message} (SocketErrorCode: {ex.SocketErrorCode})");
        }
    }

    /// <summary>
    /// Single listener that receives both multicast and broadcast messages.
    /// Joins the multicast group to receive multicast, and also receives broadcast
    /// since it binds to IPAddress.Any.
    /// </summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            using var listener = new UdpClient();
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));

            // Join multicast group to receive multicast messages
            var joined = false;
            try
            {
                listener.JoinMulticastGroup(MulticastGroup);
                joined = true;
            }
            catch
            {
                // Try joining on each interface individually
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    if (!ni.SupportsMulticast) continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        try
                        {
                            listener.JoinMulticastGroup(MulticastGroup, addr.Address);
                            joined = true;
                        }
                        catch { /* skip this interface */ }
                    }
                }
            }

            OnStatusChanged?.Invoke($"Listening on UDP port {_discoveryPort} (multicast: {(joined ? "yes" : "no")}, broadcast: yes)");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await listener.ReceiveAsync(ct);
                    ProcessDiscoveryMessage(result);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex)
                {
                    OnError?.Invoke($"Listen error: {ex.Message} (SocketErrorCode: {ex.SocketErrorCode})");
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Listen error: {ex.Message}");
                }
            }
        }
        catch (SocketException ex)
        {
            OnError?.Invoke($"Cannot start listener on port {_discoveryPort}: {ex.Message} (SocketErrorCode: {ex.SocketErrorCode})");
        }
    }

    /// <summary>
    /// Process a received UDP discovery message and register the peer.
    /// </summary>
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
                // Update LastSeen for existing peers
                _peers[peerId].LastSeen = DateTime.UtcNow;
            }
        }
        catch { /* ignore malformed messages */ }
    }

    /// <summary>
    /// Get subnet-directed broadcast addresses for all active IPv4 network interfaces.
    /// E.g. for 192.168.1.5/255.255.255.0, returns 192.168.1.255
    /// </summary>
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
                !x.Value.IsManual && // Don't auto-remove manual peers
                (DateTime.UtcNow - x.Value.LastSeen).TotalSeconds > _timeoutSeconds).ToList())
            {
                if (_peers.TryRemove(p.Key, out _))
                    OnPeerLost?.Invoke(p.Key);
            }
        }
    }
}
