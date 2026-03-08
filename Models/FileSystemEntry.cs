namespace FileX.Models;

public class FileSystemEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Extension { get; set; } = "";
    public bool IsReadOnly { get; set; }
}
