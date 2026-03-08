namespace FileX.Models;

public class DriveEntry
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string DriveType { get; set; } = "";
    public long TotalSize { get; set; }
    public long FreeSpace { get; set; }
    public bool IsReady { get; set; }
}
