using System.Diagnostics;
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

        // Try to add firewall rules (needs admin, silently fails if not admin)
        EnsureFirewallRules(Port, discoveryPort);

        _ = Task.Run(() => Discovery.StartAsync(_cts.Token));
        _ = Task.Run(() => StartWebServer(_cts.Token));
    }

    private async Task StartWebServer(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();

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

        ct.Register(() => _ = app.StopAsync());
        await app.RunAsync();
    }

    private static void EnsureFirewallRules(int httpPort, int udpPort)
    {
        try
        {
            // Check if rules already exist
            var checkResult = RunNetsh($"advfirewall firewall show rule name=\"FileX TCP\" verbose");
            if (checkResult.Contains($"{httpPort}")) return; // rules likely exist

            // Add TCP rule for web server
            RunNetsh($"advfirewall firewall add rule name=\"FileX TCP\" dir=in action=allow protocol=TCP localport={httpPort} profile=any");
            // Add UDP rule for peer discovery
            RunNetsh($"advfirewall firewall add rule name=\"FileX UDP\" dir=in action=allow protocol=UDP localport={udpPort} profile=any");
        }
        catch
        {
            // Not running as admin - silently ignore
        }
    }

    private static string RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
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

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        base.OnExit(e);
    }
}
