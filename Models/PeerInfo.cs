namespace FileX.Models;

public class PeerInfo
{
    public string Id { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public bool IsManual { get; set; }
}
