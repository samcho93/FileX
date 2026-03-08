using System.Net.Http.Json;
using System.Text.Json;
using FileX.Models;

namespace FileX.Services;

public class PeerApiClient
{
    private readonly HttpClient _http;
    private readonly FileSystemService _fs;
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public PeerApiClient(HttpClient http, FileSystemService fs)
    {
        _http = http;
        _fs = fs;
    }

    public async Task<List<DriveEntry>> GetRemoteDrives(string address) =>
        await _http.GetFromJsonAsync<List<DriveEntry>>($"{address}/api/peer/drives", Opts) ?? [];

    public async Task<List<FileSystemEntry>> GetRemoteDirectory(string address, string path) =>
        await _http.GetFromJsonAsync<List<FileSystemEntry>>(
            $"{address}/api/peer/directory?path={Uri.EscapeDataString(path)}", Opts) ?? [];

    public async Task TransferToRemote(string address, TransferItem[] items, string destDir)
    {
        foreach (var item in items)
        {
            if (item.IsDirectory) await UploadDir(address, item.FullPath, item.Name, destDir);
            else await UploadFile(address, item.FullPath, Path.Combine(destDir, item.Name));
        }
    }

    public async Task TransferFromRemote(string address, TransferItem[] items, string localDir)
    {
        foreach (var item in items)
        {
            if (item.IsDirectory) await DownloadDir(address, item.FullPath, item.Name, localDir);
            else await DownloadFile(address, item.FullPath, Path.Combine(localDir, item.Name));
        }
    }

    private async Task UploadFile(string addr, string localPath, string remotePath)
    {
        await using var stream = _fs.OpenFileForRead(localPath);
        using var content = new StreamContent(stream);
        (await _http.PostAsync($"{addr}/api/peer/file?path={Uri.EscapeDataString(remotePath)}", content)).EnsureSuccessStatusCode();
    }

    private async Task UploadDir(string addr, string localDir, string dirName, string remoteParent)
    {
        var remote = Path.Combine(remoteParent, dirName);
        await _http.PostAsync($"{addr}/api/peer/mkdir?path={Uri.EscapeDataString(remote)}", null);
        foreach (var e in _fs.GetDirectoryContents(localDir))
        {
            if (e.IsDirectory) await UploadDir(addr, e.FullPath, e.Name, remote);
            else await UploadFile(addr, e.FullPath, Path.Combine(remote, e.Name));
        }
    }

    private async Task DownloadFile(string addr, string remotePath, string localPath)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var resp = await _http.GetAsync($"{addr}/api/peer/file?path={Uri.EscapeDataString(remotePath)}");
        resp.EnsureSuccessStatusCode();
        await using var rs = await resp.Content.ReadAsStreamAsync();
        await using var ls = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await rs.CopyToAsync(ls);
    }

    private async Task DownloadDir(string addr, string remoteDir, string dirName, string localParent)
    {
        var local = Path.Combine(localParent, dirName);
        Directory.CreateDirectory(local);
        var entries = await GetRemoteDirectory(addr, remoteDir);
        foreach (var e in entries)
        {
            if (e.IsDirectory) await DownloadDir(addr, e.FullPath, e.Name, local);
            else await DownloadFile(addr, e.FullPath, Path.Combine(local, e.Name));
        }
    }
}
