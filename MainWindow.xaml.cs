using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileX.Models;

namespace FileX;

public partial class MainWindow : Window
{
    // Panel state
    private string _leftPath = "", _rightPath = "";
    private bool _leftShowDrives = true, _rightShowDrives = true;
    private string? _rightPeerId, _rightPeerAddress;
    private List<FileSystemEntry> _leftEntries = [], _rightEntries = [];
    private Point _dragStart;
    private bool _isDragging;

    // Display items
    class FileItem
    {
        public string Icon { get; set; } = "";
        public string Name { get; set; } = "";
        public string SizeText { get; set; } = "";
        public string DateText { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDir { get; set; }
        public Brush NameColor { get; set; } = Brushes.White;
    }

    class DriveItem
    {
        public string Icon { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Detail { get; set; } = "";
        public string DriveName { get; set; } = "";
        public double UsedPercent { get; set; }
    }

    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x6c, 0x6c, 0xf0));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xf0));

    public MainWindow()
    {
        InitializeComponent();
        TxtMachineName.Text = Environment.MachineName;
        LeftTitle.Text = Environment.MachineName + " (Local)";

        App.Discovery.OnPeerDiscovered += p => Dispatcher.Invoke(() => AddPeer(p));
        App.Discovery.OnPeerLost += id => Dispatcher.Invoke(() => RemovePeer(id));
        App.Discovery.OnError += msg => Dispatcher.Invoke(() => ShowStatus($"⚠ {msg}"));
        App.Discovery.OnStatusChanged += msg => Dispatcher.Invoke(() => ShowStatus(msg));

        Loaded += async (_, _) => await LeftLoadDrives();
    }

    private void ShowStatus(string msg)
    {
        TxtStatus.Text = msg;
    }

    // ===== Left panel (local) =====

    private Task LeftLoadDrives()
    {
        _leftShowDrives = true;
        _leftPath = "";
        LeftBreadcrumb.Text = "Drives";
        LeftFileList.Visibility = Visibility.Collapsed;
        LeftEmpty.Visibility = Visibility.Collapsed;
        LeftDriveList.Visibility = Visibility.Visible;

        var drives = App.FileSystem.GetDrives();
        LeftDriveList.Items.Clear();
        foreach (var d in drives)
        {
            var pct = d.TotalSize > 0 ? (double)(d.TotalSize - d.FreeSpace) / d.TotalSize * 100 : 0;
            LeftDriveList.Items.Add(MakeDrivePanel(d, pct));
        }
        return Task.CompletedTask;
    }

    private Task LeftNavigateTo(string path)
    {
        try
        {
            var entries = App.FileSystem.GetDirectoryContents(path);
            _leftPath = path;
            _leftEntries = entries;
            _leftShowDrives = false;
            LeftDriveList.Visibility = Visibility.Collapsed;
            ShowEntries(LeftFileList, LeftEmpty, entries);
            LeftBreadcrumb.Text = path;
        }
        catch (Exception ex) { ShowEmpty(LeftFileList, LeftDriveList, LeftEmpty, ex.Message); }
        return Task.CompletedTask;
    }

    // ===== Right panel (remote) =====

    private async Task RightLoadDrives()
    {
        if (_rightPeerAddress == null) return;
        _rightShowDrives = true;
        _rightPath = "";
        RightBreadcrumb.Text = "Drives";
        RightFileList.Visibility = Visibility.Collapsed;
        RightEmpty.Visibility = Visibility.Collapsed;
        RightDriveList.Visibility = Visibility.Visible;

        try
        {
            var drives = await App.Api.GetRemoteDrives(_rightPeerAddress);
            RightDriveList.Items.Clear();
            foreach (var d in drives)
            {
                var pct = d.TotalSize > 0 ? (double)(d.TotalSize - d.FreeSpace) / d.TotalSize * 100 : 0;
                RightDriveList.Items.Add(MakeDrivePanel(d, pct));
            }
        }
        catch (Exception ex) { ShowEmpty(RightFileList, RightDriveList, RightEmpty, ex.Message); }
    }

    private async Task RightNavigateTo(string path)
    {
        if (_rightPeerAddress == null) return;
        try
        {
            var entries = await App.Api.GetRemoteDirectory(_rightPeerAddress, path);
            _rightPath = path;
            _rightEntries = entries;
            _rightShowDrives = false;
            RightDriveList.Visibility = Visibility.Collapsed;
            ShowEntries(RightFileList, RightEmpty, entries);
            RightBreadcrumb.Text = path;
        }
        catch (Exception ex) { ShowEmpty(RightFileList, RightDriveList, RightEmpty, ex.Message); }
    }

    // ===== Shared helpers =====

    private void ShowEntries(ListView lv, TextBlock empty, List<FileSystemEntry> entries)
    {
        lv.Items.Clear();
        if (entries.Count == 0)
        {
            lv.Visibility = Visibility.Collapsed;
            empty.Text = "Empty folder";
            empty.Visibility = Visibility.Visible;
            return;
        }
        empty.Visibility = Visibility.Collapsed;
        lv.Visibility = Visibility.Visible;
        foreach (var e in entries)
        {
            lv.Items.Add(new FileItem
            {
                Icon = GetIcon(e),
                Name = e.Name,
                SizeText = e.IsDirectory ? "" : FormatSize(e.Size),
                DateText = e.LastModified.ToString("yyyy-MM-dd HH:mm"),
                FullPath = e.FullPath,
                IsDir = e.IsDirectory,
                NameColor = e.IsDirectory ? AccentBrush : TextBrush
            });
        }
    }

    private void ShowEmpty(ListView lv, ListBox lb, TextBlock empty, string msg)
    {
        lv.Visibility = Visibility.Collapsed;
        lb.Visibility = Visibility.Collapsed;
        empty.Text = msg;
        empty.Visibility = Visibility.Visible;
    }

    private StackPanel MakeDrivePanel(DriveEntry d, double pct)
    {
        var icon = d.DriveType switch { "Fixed" => "\U0001F4BE", "Removable" => "\U0001F50C", "CDRom" => "\U0001F4BF", _ => "\U0001F4BE" };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Tag = d.Name };
        sp.Children.Add(new TextBlock { Text = icon, FontSize = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = $"{d.Name} {(string.IsNullOrEmpty(d.Label) ? "" : $"({d.Label})")}", FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = TextBrush });
        info.Children.Add(new TextBlock { Text = $"{FormatSize(d.FreeSpace)} free of {FormatSize(d.TotalSize)} - {d.DriveType}", FontSize = 11, Foreground = (Brush)FindResource("TextMuted") });
        var bar = new Border { Width = 100, Height = 4, Background = (Brush)FindResource("BgPrimary"), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 3, 0, 0) };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = pct, Background = pct > 90 ? Brushes.Red : pct > 75 ? Brushes.Orange : AccentBrush };
        bar.Child = fill;
        info.Children.Add(bar);
        sp.Children.Add(info);
        return sp;
    }

    // ===== Button clicks =====

    private async void LeftUp_Click(object s, RoutedEventArgs e) => await NavigateUp(true);
    private async void LeftRefresh_Click(object s, RoutedEventArgs e) { if (_leftShowDrives) await LeftLoadDrives(); else await LeftNavigateTo(_leftPath); }
    private async void RightUp_Click(object s, RoutedEventArgs e) => await NavigateUp(false);
    private async void RightRefresh_Click(object s, RoutedEventArgs e) { if (_rightShowDrives) await RightLoadDrives(); else await RightNavigateTo(_rightPath); }

    private async Task NavigateUp(bool isLeft)
    {
        var path = isLeft ? _leftPath : _rightPath;
        var showDrives = isLeft ? _leftShowDrives : _rightShowDrives;
        if (showDrives) return;

        var sep = path.Contains('/') ? '/' : '\\';
        var parts = path.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            if (isLeft) await LeftLoadDrives(); else await RightLoadDrives();
        }
        else
        {
            var parent = string.Join(sep, parts[..^1]);
            if (parent.Length == 2 && parent[1] == ':') parent += sep;
            if (isLeft) await LeftNavigateTo(parent); else await RightNavigateTo(parent);
        }
    }

    // ===== Manual connect =====

    private async void ManualConnect_Click(object s, RoutedEventArgs e) => await DoManualConnect();
    private async void TxtManualIp_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await DoManualConnect();
    }

    private async Task DoManualConnect()
    {
        var ip = TxtManualIp.Text.Trim();
        if (string.IsNullOrEmpty(ip)) return;
        ShowStatus($"Connecting to {ip}...");
        var peer = await App.Discovery.AddManualPeerAsync(ip);
        if (peer != null)
        {
            TxtManualIp.Text = "";
            ShowStatus($"Connected to {peer.MachineName} ({ip})");
        }
    }

    // ===== Double-click =====

    private async void LeftDriveList_MouseDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (LeftDriveList.SelectedItem is StackPanel sp && sp.Tag is string name) await LeftNavigateTo(name);
    }

    private async void RightDriveList_MouseDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (RightDriveList.SelectedItem is StackPanel sp && sp.Tag is string name) await RightNavigateTo(name);
    }

    private async void LeftFileList_MouseDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (LeftFileList.SelectedItem is FileItem f && f.IsDir) await LeftNavigateTo(f.FullPath);
    }

    private async void RightFileList_MouseDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (RightFileList.SelectedItem is FileItem f && f.IsDir) await RightNavigateTo(f.FullPath);
    }

    // ===== Peers =====

    private readonly List<PeerInfo> _peers = [];

    private void AddPeer(PeerInfo peer)
    {
        var existing = _peers.FindIndex(p => p.Id == peer.Id);
        if (existing >= 0) _peers[existing] = peer; else _peers.Add(peer);
        RenderPeers();
    }

    private void RemovePeer(string id)
    {
        _peers.RemoveAll(p => p.Id == id);
        if (_rightPeerId == id) { _rightPeerId = null; _rightPeerAddress = null; RightTitle.Text = "Remote (select a peer)"; RightEmpty.Text = "Select a peer"; }
        RenderPeers();
    }

    private void RenderPeers()
    {
        PeerList.Items.Clear();
        TxtNoPeers.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in _peers)
        {
            var btn = new Button
            {
                Content = "\u2B24 " + p.MachineName,
                Tag = p.Id,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 12,
                Background = _rightPeerId == p.Id ? AccentBrush : (Brush)FindResource("BgTertiary"),
                Foreground = TextBrush,
                ToolTip = p.Address
            };
            btn.Click += PeerChip_Click;
            PeerList.Items.Add(btn);
        }
    }

    private async void PeerChip_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is string id)
        {
            var peer = _peers.Find(p => p.Id == id);
            if (peer == null) return;
            _rightPeerId = id;
            _rightPeerAddress = peer.Address;
            RightTitle.Text = peer.MachineName + " (Remote)";
            RenderPeers();
            await RightLoadDrives();
        }
    }

    // ===== Drag & Drop =====

    private void FileList_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _isDragging = false;
    }

    private void FileList_PreviewMouseMove(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        var diff = _dragStart - pos;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (_isDragging) return;
        _isDragging = true;

        var lv = s as ListView;
        if (lv == null || lv.SelectedItems.Count == 0) return;

        var items = lv.SelectedItems.Cast<FileItem>().Select(f => new TransferItem
        {
            Name = f.Name, FullPath = f.FullPath, IsDirectory = f.IsDir
        }).ToArray();

        bool isLeft = lv == LeftFileList;
        var data = new DataObject();
        data.SetData("FileXItems", items);
        data.SetData("FileXSource", isLeft ? "left" : "right");
        data.SetData("FileXPeerId", isLeft ? "" : (_rightPeerId ?? ""));
        data.SetData("FileXPeerAddr", isLeft ? "" : (_rightPeerAddress ?? ""));

        DragDrop.DoDragDrop(lv, data, DragDropEffects.Copy);
        _isDragging = false;
    }

    private void FileList_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("FileXItems") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void LeftFileList_Drop(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("FileXItems")) return;
        var source = e.Data.GetData("FileXSource") as string;
        if (source == "left") return;
        if (_leftShowDrives) { MessageBox.Show("Please navigate to a folder first."); return; }

        var items = e.Data.GetData("FileXItems") as TransferItem[];
        var peerAddr = e.Data.GetData("FileXPeerAddr") as string;
        if (items == null || string.IsNullOrEmpty(peerAddr)) return;

        try
        {
            await App.Api.TransferFromRemote(peerAddr, items, _leftPath);
            await LeftNavigateTo(_leftPath);
        }
        catch (Exception ex) { MessageBox.Show("Transfer failed: " + ex.Message); }
    }

    private async void RightFileList_Drop(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("FileXItems")) return;
        var source = e.Data.GetData("FileXSource") as string;
        if (source == "right") return;
        if (_rightShowDrives || _rightPeerAddress == null) { MessageBox.Show("Please navigate to a folder first."); return; }

        var items = e.Data.GetData("FileXItems") as TransferItem[];
        if (items == null) return;

        try
        {
            await App.Api.TransferToRemote(_rightPeerAddress, items, _rightPath);
            await RightNavigateTo(_rightPath);
        }
        catch (Exception ex) { MessageBox.Show("Transfer failed: " + ex.Message); }
    }

    // ===== Utilities =====

    private static string FormatSize(long bytes)
    {
        if (bytes == 0) return "";
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        int i = (int)Math.Floor(Math.Log(bytes, 1024));
        return $"{bytes / Math.Pow(1024, i):F1} {u[i]}";
    }

    private static string GetIcon(FileSystemEntry e)
    {
        if (e.IsDirectory) return "\U0001F4C1";
        return (e.Extension?.ToLower()) switch
        {
            ".txt" or ".log" or ".md" => "\U0001F4C4",
            ".pdf" => "\U0001F4C5",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" => "\U0001F5BC",
            ".mp4" or ".avi" or ".mkv" or ".mov" => "\U0001F3AC",
            ".mp3" or ".wav" or ".flac" => "\U0001F3B5",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\U0001F4E6",
            ".exe" or ".msi" => "\u2699",
            ".js" or ".ts" or ".py" or ".cs" or ".java" or ".cpp" or ".c" => "\U0001F4DC",
            ".html" or ".css" => "\U0001F310",
            ".json" or ".xml" or ".yaml" => "\U0001F4CB",
            _ => "\U0001F4C4"
        };
    }
}
