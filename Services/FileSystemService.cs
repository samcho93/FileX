using FileX.Models;

namespace FileX.Services;

public class FileSystemService
{
    public List<DriveEntry> GetDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DriveEntry
            {
                Name = d.Name,
                Label = d.VolumeLabel,
                DriveType = d.DriveType.ToString(),
                TotalSize = d.TotalSize,
                FreeSpace = d.AvailableFreeSpace,
                IsReady = d.IsReady
            })
            .ToList();
    }

    public List<FileSystemEntry> GetDirectoryContents(string path)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(path));
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var entries = new List<FileSystemEntry>();

        try
        {
            foreach (var d in dir.GetDirectories())
            {
                try
                {
                    entries.Add(new FileSystemEntry
                    {
                        Name = d.Name,
                        FullPath = d.FullName,
                        IsDirectory = true,
                        Size = 0,
                        LastModified = d.LastWriteTime,
                        Extension = "",
                        IsReadOnly = d.Attributes.HasFlag(FileAttributes.ReadOnly)
                    });
                }
                catch (UnauthorizedAccessException) { }
            }

            foreach (var f in dir.GetFiles())
            {
                try
                {
                    entries.Add(new FileSystemEntry
                    {
                        Name = f.Name,
                        FullPath = f.FullName,
                        IsDirectory = false,
                        Size = f.Length,
                        LastModified = f.LastWriteTime,
                        Extension = f.Extension,
                        IsReadOnly = f.IsReadOnly
                    });
                }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (UnauthorizedAccessException) { }

        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Stream OpenFileForRead(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 524288, true);
    }

    public Stream OpenFileForWrite(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var fullPath = Path.GetFullPath(path);
        return new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 524288, true);
    }

    public long GetFileSize(string path)
    {
        return new FileInfo(Path.GetFullPath(path)).Length;
    }

    public long GetTotalSize(string[] paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                total += new FileInfo(fullPath).Length;
            }
            else if (Directory.Exists(fullPath))
            {
                total += GetDirectorySize(fullPath);
            }
        }
        return total;
    }

    public List<string> GetAllFiles(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
            return [fullPath];

        if (Directory.Exists(fullPath))
        {
            return Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories).ToList();
        }

        return [];
    }

    private long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; }
                catch { }
            }
        }
        catch { }
        return size;
    }
}
