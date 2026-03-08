using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using FileX.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FileX;

public partial class App : Application
{
    public static FileSystemService FileSystem { get; } = new();
    public static PeerDiscoveryService Discovery { get; private set; } = null!;
    public static PeerApiClient Api { get; private set; } = null!;
    public static HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(30) };
    public static int Port { get; private set; } = 5000;
    public static event Action<string>? OnAppStatus;
    public static bool WebServerReady { get; private set; }

    private readonly CancellationTokenSource _cts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int discoveryPort = 5001;
        var cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        if (File.Exists(cfgPath))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
            if (doc.RootElement.TryGetProperty("FileX", out var fx))
            {
                if (fx.TryGetProperty("Port", out var p)) Port = p.GetInt32();
                if (fx.TryGetProperty("DiscoveryPort", out var dp)) discoveryPort = dp.GetInt32();
            }
        }

        Api = new PeerApiClient(Http, FileSystem);
        Discovery = new PeerDiscoveryService(Port, discoveryPort, 10, 30);

        // Add firewall rules with UAC elevation
        EnsureFirewallRules(Port, discoveryPort);

        _ = Task.Run(async () =>
        {
            try { await Discovery.StartAsync(_cts.Token); }
            catch (Exception ex) { OnAppStatus?.Invoke($"Discovery error: {ex.Message}"); }
        });

        _ = Task.Run(async () =>
        {
            try { await StartWebServer(_cts.Token); }
            catch (Exception ex) { OnAppStatus?.Invoke($"Web server error: {ex.Message}"); }
        });
    }

    private async Task StartWebServer(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        // Peer info endpoint (returns machine name)
        app.MapGet("/api/peer/info", () => new { machineName = Environment.MachineName });

        app.MapGet("/api/peer/drives", () => FileSystem.GetDrives());
        app.MapGet("/api/peer/directory", (string path) => FileSystem.GetDirectoryContents(path));
        app.MapGet("/api/peer/file", async (string path, HttpContext ctx) =>
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "application/octet-stream";
            await using var s = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await s.CopyToAsync(ctx.Response.Body);
        });
        app.MapPost("/api/peer/file", async (string path, HttpContext ctx) =>
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await using var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await ctx.Request.Body.CopyToAsync(fs);
        });
        app.MapPost("/api/peer/mkdir", (string path) => Directory.CreateDirectory(Path.GetFullPath(path)));

        // Bidirectional peer registration: when a remote peer connects to us, it sends its info
        app.MapPost("/api/peer/connect", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
                var name = body.GetProperty("machineName").GetString() ?? "Unknown";
                var peerPort = body.GetProperty("port").GetInt32();
                var ip = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "";
                if (!string.IsNullOrEmpty(ip))
                {
                    Discovery.RegisterIncomingPeer(ip, peerPort, name);
                }
                ctx.Response.StatusCode = 200;
            }
            catch
            {
                ctx.Response.StatusCode = 400;
            }
        });

        WebServerReady = true;
        OnAppStatus?.Invoke($"Web server started on port {Port}");

        ct.Register(() => _ = app.StopAsync());
        await app.RunAsync();
    }

    private static void EnsureFirewallRules(int httpPort, int udpPort)
    {
        try
        {
            // Check if rules already exist (this works without admin)
            var checkTcp = RunCmd("netsh", $"advfirewall firewall show rule name=\"FileX TCP\"");
            var checkUdp = RunCmd("netsh", $"advfirewall firewall show rule name=\"FileX UDP\"");

            bool tcpExists = checkTcp.Contains("FileX TCP");
            bool udpExists = checkUdp.Contains("FileX UDP");

            if (tcpExists && udpExists) return;

            // Build netsh commands to run with elevation
            var commands = new List<string>();
            if (!tcpExists)
            {
                commands.Add($"netsh advfirewall firewall add rule name=\"FileX TCP\" dir=in action=allow protocol=TCP localport={httpPort} profile=any");
            }
            if (!udpExists)
            {
                commands.Add($"netsh advfirewall firewall add rule name=\"FileX UDP\" dir=in action=allow protocol=UDP localport={udpPort} profile=any");
            }

            // Run with UAC elevation via cmd /c
            var cmdArgs = string.Join(" & ", commands);
            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmdArgs}")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            var p = Process.Start(psi);
            p?.WaitForExit(10000);
            OnAppStatus?.Invoke("Firewall rules added successfully");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled UAC prompt
            OnAppStatus?.Invoke("Firewall rules not added (UAC cancelled). Manual connection may be needed.");
        }
        catch (Exception ex)
        {
            OnAppStatus?.Invoke($"Firewall setup: {ex.Message}");
        }
    }

    private static string RunCmd(string fileName, string args)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var p = Process.Start(psi);
        if (p == null) return "";
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    /// <summary>
    /// Get the local LAN IP addresses
    /// </summary>
    public static List<string> GetLocalIPs()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ips.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch { }
        return ips;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        base.OnExit(e);
    }
}
