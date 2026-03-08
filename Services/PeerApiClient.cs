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

    /// <summary>
    /// Transfer files to remote peer with per-file progress reporting.
    /// </summary>
    public async Task TransferToRemote(string address, TransferItem[] items, string destDir,
        Action<TransferProgressInfo>? onFileStart = null,
        Action<TransferProgressInfo>? onProgress = null,
        Action<TransferProgressInfo>? onFileComplete = null)
    {
        foreach (var item in items)
        {
            if (item.IsDirectory)
                await UploadDir(address, item.FullPath, item.Name, destDir, onFileStart, onProgress, onFileComplete);
            else
            {
                var remotePath = Path.Combine(destDir, item.Name);
                var fileSize = _fs.GetFileSize(item.FullPath);
                var info = new TransferProgressInfo
                {
                    FileName = item.Name,
                    TotalBytes = fileSize,
                    Direction = "\u2191 Upload",
                    Status = TransferStatus.InProgress
                };
                onFileStart?.Invoke(info);

                try
                {
                    await UploadFileWithProgress(address, item.FullPath, remotePath, info, onProgress);
                    info.Status = TransferStatus.Completed;
                    info.StatusText = $"Completed - {FormatSize(fileSize)}";
                    onFileComplete?.Invoke(info);
                }
                catch (Exception ex)
                {
                    info.Status = TransferStatus.Failed;
                    info.StatusText = $"Failed: {ex.Message}";
                    onFileComplete?.Invoke(info);
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Transfer files from remote peer with per-file progress reporting.
    /// </summary>
    public async Task TransferFromRemote(string address, TransferItem[] items, string localDir,
        Action<TransferProgressInfo>? onFileStart = null,
        Action<TransferProgressInfo>? onProgress = null,
        Action<TransferProgressInfo>? onFileComplete = null)
    {
        foreach (var item in items)
        {
            if (item.IsDirectory)
                await DownloadDir(address, item.FullPath, item.Name, localDir, onFileStart, onProgress, onFileComplete);
            else
            {
                var localPath = Path.Combine(localDir, item.Name);
                var info = new TransferProgressInfo
                {
                    FileName = item.Name,
                    TotalBytes = item.Size,
                    Direction = "\u2193 Download",
                    Status = TransferStatus.InProgress
                };
                onFileStart?.Invoke(info);

                try
                {
                    await DownloadFileWithProgress(address, item.FullPath, localPath, info, onProgress);
                    info.Status = TransferStatus.Completed;
                    info.StatusText = $"Completed - {FormatSize(info.TotalBytes)}";
                    onFileComplete?.Invoke(info);
                }
                catch (Exception ex)
                {
                    info.Status = TransferStatus.Failed;
                    info.StatusText = $"Failed: {ex.Message}";
                    onFileComplete?.Invoke(info);
                    throw;
                }
            }
        }
    }

    private async Task UploadFileWithProgress(string addr, string localPath, string remotePath,
        TransferProgressInfo info, Action<TransferProgressInfo>? onProgress)
    {
        await using var fileStream = _fs.OpenFileForRead(localPath);
        var progressStream = new ProgressStream(fileStream, bytesRead =>
        {
            info.BytesTransferred = bytesRead;
            info.StatusText = $"{FormatSize(bytesRead)} / {FormatSize(info.TotalBytes)}";
            onProgress?.Invoke(info);
        });

        using var content = new StreamContent(progressStream, 81920);
        var resp = await _http.PostAsync(
            $"{addr}/api/peer/file?path={Uri.EscapeDataString(remotePath)}", content);
        resp.EnsureSuccessStatusCode();
    }

    private async Task DownloadFileWithProgress(string addr, string remotePath, string localPath,
        TransferProgressInfo info, Action<TransferProgressInfo>? onProgress)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        using var resp = await _http.GetAsync(
            $"{addr}/api/peer/file?path={Uri.EscapeDataString(remotePath)}",
            HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        // Get content length if available
        var contentLength = resp.Content.Headers.ContentLength;
        if (contentLength.HasValue && info.TotalBytes == 0)
            info.TotalBytes = contentLength.Value;

        await using var rs = await resp.Content.ReadAsStreamAsync();
        await using var ls = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        await StreamCopyHelper.CopyWithProgressAsync(rs, ls, bytesRead =>
        {
            info.BytesTransferred = bytesRead;
            info.StatusText = info.TotalBytes > 0
                ? $"{FormatSize(bytesRead)} / {FormatSize(info.TotalBytes)}"
                : $"{FormatSize(bytesRead)}";
            onProgress?.Invoke(info);
        });

        // Update total if we didn't know it before
        if (info.TotalBytes == 0)
            info.TotalBytes = info.BytesTransferred;
    }

    private async Task UploadDir(string addr, string localDir, string dirName, string remoteParent,
        Action<TransferProgressInfo>? onFileStart, Action<TransferProgressInfo>? onProgress,
        Action<TransferProgressInfo>? onFileComplete)
    {
        var remote = Path.Combine(remoteParent, dirName);
        await _http.PostAsync($"{addr}/api/peer/mkdir?path={Uri.EscapeDataString(remote)}", null);

        foreach (var e in _fs.GetDirectoryContents(localDir))
        {
            if (e.IsDirectory)
                await UploadDir(addr, e.FullPath, e.Name, remote, onFileStart, onProgress, onFileComplete);
            else
            {
                var remotePath = Path.Combine(remote, e.Name);
                var fileSize = e.Size;
                var info = new TransferProgressInfo
                {
                    FileName = e.Name,
                    TotalBytes = fileSize,
                    Direction = "\u2191 Upload",
                    Status = TransferStatus.InProgress
                };
                onFileStart?.Invoke(info);

                try
                {
                    await UploadFileWithProgress(addr, e.FullPath, remotePath, info, onProgress);
                    info.Status = TransferStatus.Completed;
                    info.StatusText = $"Completed - {FormatSize(fileSize)}";
                    onFileComplete?.Invoke(info);
                }
                catch (Exception ex)
                {
                    info.Status = TransferStatus.Failed;
                    info.StatusText = $"Failed: {ex.Message}";
                    onFileComplete?.Invoke(info);
                    throw;
                }
            }
        }
    }

    private async Task DownloadDir(string addr, string remoteDir, string dirName, string localParent,
        Action<TransferProgressInfo>? onFileStart, Action<TransferProgressInfo>? onProgress,
        Action<TransferProgressInfo>? onFileComplete)
    {
        var local = Path.Combine(localParent, dirName);
        Directory.CreateDirectory(local);
        var entries = await GetRemoteDirectory(addr, remoteDir);

        foreach (var e in entries)
        {
            if (e.IsDirectory)
                await DownloadDir(addr, e.FullPath, e.Name, local, onFileStart, onProgress, onFileComplete);
            else
            {
                var localPath = Path.Combine(local, e.Name);
                var info = new TransferProgressInfo
                {
                    FileName = e.Name,
                    TotalBytes = e.Size,
                    Direction = "\u2193 Download",
                    Status = TransferStatus.InProgress
                };
                onFileStart?.Invoke(info);

                try
                {
                    await DownloadFileWithProgress(addr, e.FullPath, localPath, info, onProgress);
                    info.Status = TransferStatus.Completed;
                    info.StatusText = $"Completed - {FormatSize(info.TotalBytes)}";
                    onFileComplete?.Invoke(info);
                }
                catch (Exception ex)
                {
                    info.Status = TransferStatus.Failed;
                    info.StatusText = $"Failed: {ex.Message}";
                    onFileComplete?.Invoke(info);
                    throw;
                }
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = (int)Math.Floor(Math.Log(bytes, 1024));
        if (i >= units.Length) i = units.Length - 1;
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }
}
