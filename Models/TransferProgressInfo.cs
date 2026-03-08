using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileX.Models;

public enum TransferStatus { Pending, InProgress, Completed, Failed }

public class TransferProgressInfo : INotifyPropertyChanged
{
    private string _fileName = "";
    private long _totalBytes;
    private long _bytesTransferred;
    private string _statusText = "";
    private TransferStatus _status = TransferStatus.Pending;
    private string _direction = "";

    public string FileName
    {
        get => _fileName;
        set { _fileName = value; OnPropertyChanged(); }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set { _totalBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(Percent)); }
    }

    public long BytesTransferred
    {
        get => _bytesTransferred;
        set { _bytesTransferred = value; OnPropertyChanged(); OnPropertyChanged(nameof(Percent)); }
    }

    public double Percent => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public TransferStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string Direction
    {
        get => _direction;
        set { _direction = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
