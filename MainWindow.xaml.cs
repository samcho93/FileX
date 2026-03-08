using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileX.Controls;
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
    // Drag data stored in fields to avoid WPF OLE serialization crash
    private TransferItem[]? _dragItems;
    private string _dragSource = "";
    private string _dragPeerAddr = "";

    // Marquee (rubber-band) selection
    private bool _isMarqueeSelecting;
    private Point _marqueeStart;
    private ListView? _marqueeListView;
    private MarqueeAdorner? _marqueeAdorner;

    // Transfer progress tracking
    private readonly ObservableCollection<TransferProgressInfo> _transfers = [];

    // Display items
    class FileItem
    {
        public string Icon { get; set; } = "";
        public string Name { get; set; } = "";
        public string SizeText { get; set; } = "";
        public string DateText { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDir { get; set; }
        public long Size { get; set; }
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

    // Debounce timer for auto-refresh when files are received via API
    private readonly DispatcherTimer _refreshTimer;

    // FileSystemWatcher for real-time folder monitoring
    private FileSystemWatcher? _leftWatcher;
    private readonly DispatcherTimer _watcherRefreshTimer;

    public MainWindow()
    {
        InitializeComponent();
        TxtMachineName.Text = Environment.MachineName;
        LeftTitle.Text = Environment.MachineName + " (Local)";

        // Bind transfer list
        TransferList.ItemsSource = _transfers;

        // Debounced refresh: waits 500ms after last file received before refreshing
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            if (!_leftShowDrives && !string.IsNullOrEmpty(_leftPath))
                await LeftNavigateTo(_leftPath);
        };

        // Debounced refresh for FileSystemWatcher events
        _watcherRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _watcherRefreshTimer.Tick += async (_, _) =>
        {
            _watcherRefreshTimer.Stop();
            if (!_leftShowDrives && !string.IsNullOrEmpty(_leftPath))
                await LeftNavigateTo(_leftPath);
        };

        App.Discovery.OnPeerDiscovered += p => Dispatcher.Invoke(() => AddPeer(p));
        App.Discovery.OnPeerLost += id => Dispatcher.Invoke(() => RemovePeer(id));
        App.Discovery.OnError += msg => Dispatcher.Invoke(() => ShowStatus($"\u26a0 {msg}"));
        App.Discovery.OnStatusChanged += msg => Dispatcher.Invoke(() => ShowStatus(msg));
        App.OnAppStatus += msg => Dispatcher.Invoke(() => ShowStatus(msg));

        // Auto-refresh left panel when files are received from a remote peer
        App.OnFileReceived += dir => Dispatcher.Invoke(() =>
        {
            if (!_leftShowDrives && !string.IsNullOrEmpty(_leftPath))
            {
                try
                {
                    var leftFull = Path.GetFullPath(_leftPath);
                    var dirFull = Path.GetFullPath(dir);
                    if (string.Equals(leftFull, dirFull, StringComparison.OrdinalIgnoreCase) ||
                        dirFull.StartsWith(leftFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        _refreshTimer.Stop();
                        _refreshTimer.Start(); // debounce: restart timer
                    }
                }
                catch { }
            }
        });

        Loaded += async (_, _) =>
        {
            await LeftLoadDrives();
            ShowLocalInfo();
        };
    }

    private void ShowLocalInfo()
    {
        var ips = App.GetLocalIPs();
        var ipText = ips.Count > 0 ? string.Join(", ", ips) : "No network";
        TxtMachineName.Text = $"{Environment.MachineName}  |  {ipText}:{App.Port}";
        ShowStatus($"Ready \u2014 Other PCs can connect to: {ipText}:{App.Port}");
    }

    private void ShowStatus(string msg)
    {
        TxtStatus.Text = msg;
    }

    // ===== Transfer progress helpers =====

    private void OnTransferFileStart(TransferProgressInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            _transfers.Add(info);
            TransferPanel.Visibility = Visibility.Visible;
        });
    }

    private void OnTransferProgress(TransferProgressInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            // Force UI update by raising property changed (already handled by INotifyPropertyChanged)
            // The binding will pick up the changes automatically
        });
    }

    private void OnTransferFileComplete(TransferProgressInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            // Auto-remove completed transfers after 3 seconds
            if (info.Status == TransferStatus.Completed)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _transfers.Remove(info);
                    if (_transfers.Count == 0)
                        TransferPanel.Visibility = Visibility.Collapsed;
                };
                timer.Start();
            }
        });
    }

    private void ClearTransfers_Click(object s, RoutedEventArgs e)
    {
        _transfers.Clear();
        TransferPanel.Visibility = Visibility.Collapsed;
    }

    // ===== FileSystemWatcher =====

    private void StartWatching(string path)
    {
        StopWatching();
        try
        {
            _leftWatcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _leftWatcher.Created += OnFolderChanged;
            _leftWatcher.Deleted += OnFolderChanged;
            _leftWatcher.Renamed += OnFolderChanged;
            _leftWatcher.Changed += OnFolderChanged;
        }
        catch { /* ignore if we can't watch (e.g. network drive) */ }
    }

    private void StopWatching()
    {
        if (_leftWatcher != null)
        {
            _leftWatcher.EnableRaisingEvents = false;
            _leftWatcher.Dispose();
            _leftWatcher = null;
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _watcherRefreshTimer.Stop();
            _watcherRefreshTimer.Start();
        });
    }

    // ===== Left panel (local) =====

    private Task LeftLoadDrives()
    {
        StopWatching();
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
            StartWatching(path);
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
                Size = e.Size,
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
        // Use text symbols with explicit light foreground for dark theme readability
        var icon = d.DriveType switch
        {
            "Fixed" => "\u25a0",      // filled square
            "Removable" => "\u25c6",  // diamond
            "CDRom" => "\u25cb",      // circle
            "Network" => "\u25b2",    // triangle
            _ => "\u25a0"
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Tag = d.Name };
        sp.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 20,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AccentBrush
        });
        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = $"{d.Name} {(string.IsNullOrEmpty(d.Label) ? "" : $"({d.Label})")}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = TextBrush
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{FormatSize(d.FreeSpace)} free of {FormatSize(d.TotalSize)} - {d.DriveType}",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextMuted")
        });
        var bar = new Border
        {
            Width = 100,
            Height = 4,
            Background = (Brush)FindResource("BgPrimary"),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 3, 0, 0)
        };
        var fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = pct,
            Background = pct > 90 ? Brushes.Red : pct > 75 ? Brushes.Orange : AccentBrush
        };
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

            // Auto-select the peer for right panel
            _rightPeerId = peer.Id;
            _rightPeerAddress = peer.Address;
            RightTitle.Text = peer.MachineName + " (Remote)";
            RenderPeers();
            await RightLoadDrives();
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

    private async void AddPeer(PeerInfo peer)
    {
        var existing = _peers.FindIndex(p => p.Id == peer.Id);
        if (existing >= 0) _peers[existing] = peer; else _peers.Add(peer);

        // Auto-select if no peer is currently selected
        if (_rightPeerId == null)
        {
            _rightPeerId = peer.Id;
            _rightPeerAddress = peer.Address;
            RightTitle.Text = peer.MachineName + " (Remote)";
            RenderPeers();
            await RightLoadDrives();
            return;
        }
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
                Content = "\u2b24 " + p.MachineName,
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

    // ===== Copy buttons =====

    private async void CopyToRight_Click(object s, RoutedEventArgs e)
    {
        if (_rightPeerAddress == null) { ShowStatus("No peer connected"); return; }
        if (_leftShowDrives) { ShowStatus("Navigate to a local folder first"); return; }
        if (_rightShowDrives) { ShowStatus("Navigate to a remote folder first"); return; }

        var items = LeftFileList.SelectedItems.Cast<FileItem>()
            .Select(f => new TransferItem { Name = f.Name, FullPath = f.FullPath, IsDirectory = f.IsDir, Size = f.Size })
            .ToArray();
        if (items.Length == 0) { ShowStatus("Select files to send"); return; }

        ShowStatus($"Uploading {items.Length} item(s)...");
        try
        {
            await App.Api.TransferToRemote(_rightPeerAddress, items, _rightPath,
                OnTransferFileStart, OnTransferProgress, OnTransferFileComplete);
            ShowStatus($"Upload complete \u2014 {items.Length} item(s)");
            await RightNavigateTo(_rightPath);
        }
        catch (Exception ex) { ShowStatus($"Transfer failed: {ex.Message}"); }
    }

    private async void CopyToLeft_Click(object s, RoutedEventArgs e)
    {
        if (_rightPeerAddress == null) { ShowStatus("No peer connected"); return; }
        if (_leftShowDrives) { ShowStatus("Navigate to a local folder first"); return; }
        if (_rightShowDrives) { ShowStatus("Navigate to a remote folder first"); return; }

        var items = RightFileList.SelectedItems.Cast<FileItem>()
            .Select(f => new TransferItem { Name = f.Name, FullPath = f.FullPath, IsDirectory = f.IsDir, Size = f.Size })
            .ToArray();
        if (items.Length == 0) { ShowStatus("Select files to download"); return; }

        ShowStatus($"Downloading {items.Length} item(s)...");
        try
        {
            await App.Api.TransferFromRemote(_rightPeerAddress, items, _leftPath,
                OnTransferFileStart, OnTransferProgress, OnTransferFileComplete);
            ShowStatus($"Download complete \u2014 {items.Length} item(s)");
            await LeftNavigateTo(_leftPath);
        }
        catch (Exception ex) { ShowStatus($"Transfer failed: {ex.Message}"); }
    }

    // ===== Drag & Drop =====

    private void FileList_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (s is not ListView lv) return;
        _dragStart = e.GetPosition(null);
        _isDragging = false;

        var hit = VisualTreeHelper.HitTest(lv, e.GetPosition(lv));
        if (hit?.VisualHit == null) return;

        // Don't start marquee if clicking on column header or scrollbar
        if (FindParent<GridViewColumnHeader>(hit.VisualHit as DependencyObject) != null) return;
        if (FindParent<System.Windows.Controls.Primitives.ScrollBar>(hit.VisualHit as DependencyObject) != null) return;

        var listViewItem = FindParent<ListViewItem>(hit.VisualHit as DependencyObject);

        if (listViewItem != null)
        {
            // Clicked on an item - existing drag behavior
            if (lv.SelectedItems.Count > 1 && listViewItem.IsSelected)
                e.Handled = true; // prevent deselection
        }
        else
        {
            // Clicked on empty space - start marquee selection
            _isMarqueeSelecting = true;
            _marqueeStart = e.GetPosition(lv);
            _marqueeListView = lv;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                lv.SelectedItems.Clear();

            var adornerLayer = AdornerLayer.GetAdornerLayer(lv);
            if (adornerLayer != null)
            {
                _marqueeAdorner = new MarqueeAdorner(lv);
                adornerLayer.Add(_marqueeAdorner);
            }

            lv.CaptureMouse();
            e.Handled = true;
        }
    }

    private static T? FindParent<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj != null)
        {
            if (obj is T result) return result;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private void FileList_PreviewMouseMove(object s, MouseEventArgs e)
    {
        // Marquee selection mode
        if (_isMarqueeSelecting && _marqueeListView == s && s is ListView marquee)
        {
            var pos = e.GetPosition(marquee);
            _marqueeAdorner?.Update(_marqueeStart, pos);
            SelectItemsInMarquee(marquee, new Rect(_marqueeStart, pos));
            return;
        }

        // Drag-and-drop mode
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos2 = e.GetPosition(null);
        var diff = _dragStart - pos2;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (_isDragging) return;
        _isDragging = true;

        var lv = s as ListView;
        if (lv == null || lv.SelectedItems.Count == 0) { _isDragging = false; return; }

        bool isLeft = lv == LeftFileList;

        // Store drag data in fields (avoids WPF OLE serialization crash with custom objects)
        _dragItems = lv.SelectedItems.Cast<FileItem>().Select(f => new TransferItem
        {
            Name = f.Name, FullPath = f.FullPath, IsDirectory = f.IsDir, Size = f.Size
        }).ToArray();
        _dragSource = isLeft ? "left" : "right";
        _dragPeerAddr = isLeft ? "" : (_rightPeerAddress ?? "");

        var data = new DataObject();
        data.SetData("FileXDrag", "active"); // simple string marker for DragOver
        DragDrop.DoDragDrop(lv, data, DragDropEffects.Copy);
        _isDragging = false;
    }

    private void FileList_PreviewMouseUp(object s, MouseButtonEventArgs e)
    {
        if (_isMarqueeSelecting)
        {
            _isMarqueeSelecting = false;
            if (_marqueeAdorner != null && _marqueeListView != null)
            {
                var adornerLayer = AdornerLayer.GetAdornerLayer(_marqueeListView);
                adornerLayer?.Remove(_marqueeAdorner);
                _marqueeAdorner = null;
            }
            _marqueeListView?.ReleaseMouseCapture();
            _marqueeListView = null;
        }
    }

    private void SelectItemsInMarquee(ListView lv, Rect marqueeRect)
    {
        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        for (int i = 0; i < lv.Items.Count; i++)
        {
            if (lv.ItemContainerGenerator.ContainerFromIndex(i) is not ListViewItem item)
                continue;

            var topLeft = item.TranslatePoint(new Point(0, 0), lv);
            var itemRect = new Rect(topLeft, new Size(item.ActualWidth, item.ActualHeight));

            if (marqueeRect.IntersectsWith(itemRect))
            {
                if (!item.IsSelected) item.IsSelected = true;
            }
            else if (!ctrlHeld)
            {
                if (item.IsSelected) item.IsSelected = false;
            }
        }
    }

    private void FileList_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("FileXDrag") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void LeftFileList_Drop(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("FileXDrag") || _dragItems == null) return;
        if (_dragSource == "left") return;
        if (_leftShowDrives) { MessageBox.Show("Please navigate to a folder first."); return; }
        if (string.IsNullOrEmpty(_dragPeerAddr)) return;

        var items = _dragItems;
        var peerAddr = _dragPeerAddr;
        ShowStatus($"Downloading {items.Length} item(s)...");

        try
        {
            await App.Api.TransferFromRemote(peerAddr, items, _leftPath,
                OnTransferFileStart, OnTransferProgress, OnTransferFileComplete);
            ShowStatus($"Download complete \u2014 {items.Length} item(s)");
            await LeftNavigateTo(_leftPath);
        }
        catch (Exception ex) { ShowStatus($"Transfer failed: {ex.Message}"); MessageBox.Show("Transfer failed: " + ex.Message); }
    }

    private async void RightFileList_Drop(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("FileXDrag") || _dragItems == null) return;
        if (_dragSource == "right") return;
        if (_rightShowDrives || _rightPeerAddress == null) { MessageBox.Show("Please navigate to a folder first."); return; }

        var items = _dragItems;
        ShowStatus($"Uploading {items.Length} item(s)...");

        try
        {
            await App.Api.TransferToRemote(_rightPeerAddress, items, _rightPath,
                OnTransferFileStart, OnTransferProgress, OnTransferFileComplete);
            ShowStatus($"Upload complete \u2014 {items.Length} item(s)");
            await RightNavigateTo(_rightPath);
        }
        catch (Exception ex) { ShowStatus($"Transfer failed: {ex.Message}"); MessageBox.Show("Transfer failed: " + ex.Message); }
    }

    // ===== Delete =====

    private async void LeftDelete_Click(object s, RoutedEventArgs e) => await DeleteLeft();
    private async void RightDelete_Click(object s, RoutedEventArgs e) => await DeleteRight();

    private async void LeftFileList_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) await DeleteLeft();
    }

    private async void RightFileList_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) await DeleteRight();
    }

    private async Task DeleteLeft()
    {
        if (_leftShowDrives) return;
        var items = LeftFileList.SelectedItems.Cast<FileItem>().ToArray();
        if (items.Length == 0) { ShowStatus("Select files to delete"); return; }

        var names = string.Join("\n", items.Select(f => f.Name));
        var result = MessageBox.Show($"Delete {items.Length} item(s)?\n\n{names}", "Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            foreach (var item in items)
            {
                var full = Path.GetFullPath(item.FullPath);
                if (item.IsDir && Directory.Exists(full))
                    Directory.Delete(full, true);
                else if (File.Exists(full))
                    File.Delete(full);
            }
            ShowStatus($"Deleted {items.Length} item(s)");
            await LeftNavigateTo(_leftPath);
        }
        catch (Exception ex) { ShowStatus($"Delete failed: {ex.Message}"); }
    }

    private Task DeleteRight()
    {
        if (_rightPeerAddress == null) { ShowStatus("No peer connected"); return Task.CompletedTask; }
        if (_rightShowDrives) return Task.CompletedTask;
        var items = RightFileList.SelectedItems.Cast<FileItem>().ToArray();
        if (items.Length == 0) { ShowStatus("Select files to delete"); return Task.CompletedTask; }

        var names = string.Join("\n", items.Select(f => f.Name));
        var result = MessageBox.Show($"Delete {items.Length} item(s) on remote?\n\n{names}", "Delete Remote",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return Task.CompletedTask;

        var addr = _rightPeerAddress;
        var path = _rightPath;
        ShowStatus($"Deleting {items.Length} remote item(s)...");

        // Run deletions in background with a short-timeout HttpClient to avoid UI freeze
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var failed = 0;
            foreach (var item in items)
            {
                try
                {
                    var resp = await http.DeleteAsync(
                        $"{addr}/api/peer/file?path={Uri.EscapeDataString(item.FullPath)}");
                    resp.EnsureSuccessStatusCode();
                }
                catch { failed++; }
            }

            await Dispatcher.InvokeAsync(async () =>
            {
                if (failed > 0)
                    ShowStatus($"Deleted {items.Length - failed} item(s), {failed} failed");
                else
                    ShowStatus($"Deleted {items.Length} remote item(s)");
                await RightNavigateTo(path);
            });
        });
        return Task.CompletedTask;
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
