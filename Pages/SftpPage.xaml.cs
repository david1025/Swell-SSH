using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using SwellSSH.Models;
using SwellSSH.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace SwellSSH.Pages
{
    public sealed class SftpFileItemViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public bool IsParent { get; set; }

        public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
        public string KindText
        {
            get
            {
                if (IsParent) return "";
                if (IsDirectory) return "folder";
                var extension = Path.GetExtension(Name);
                return string.IsNullOrWhiteSpace(extension)
                    ? "file"
                    : extension.TrimStart('.').ToLowerInvariant();
            }
        }
        public string SizeText => IsDirectory ? "" : FormatSize(Size);
        public string ModifiedText => IsParent ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
        }

        public void Notify([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class SftpTransferProgressViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private string _title = "";
        private string _detail = "";
        private double _percent;
        private bool _isCancelable;

        public string Title { get => _title; set { if (_title != value) { _title = value; Notify(); } } }
        public string Detail { get => _detail; set { if (_detail != value) { _detail = value; Notify(); } } }
        public double Percent
        {
            get => _percent;
            set { var b = Math.Clamp(value, 0, 100); if (Math.Abs(_percent - b) > 0.1) { _percent = b; Notify(); Notify(nameof(PercentText)); } }
        }
        public string PercentText => $"{Percent:F0}%";
        public bool IsCancelable { get => _isCancelable; set { if (_isCancelable != value) { _isCancelable = value; Notify(); Notify(nameof(CancelVisibility)); } } }
        public Visibility CancelVisibility => IsCancelable ? Visibility.Visible : Visibility.Collapsed;
        private void Notify([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal sealed class SftpRemoteSession : IDisposable, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public ConnectionProfile Profile { get; }
        public SftpClient Client { get; }
        public ObservableCollection<SftpFileItemViewModel> Items { get; } = new();
        public string SortColumn { get; set; } = "Name";
        public bool SortAscending { get; set; } = true;
        public List<SftpBookmark> Bookmarks { get; set; } = new();
        public bool ShowHiddenFiles { get; set; }
        public string SearchText { get; set; } = "";
        private string _remotePath = ".";
        public string RemotePath
        {
            get => _remotePath;
            set { if (_remotePath != value) { _remotePath = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemotePath))); } }
        }
        public SftpRemoteSession(ConnectionProfile profile, SftpClient client) { Profile = profile; Client = client; }
        public void Dispose() { try { Client.Disconnect(); } catch { } Client.Dispose(); }
    }

    internal sealed class RemoteSortHeader { public SftpRemoteSession Session { get; } public string Column { get; } public RemoteSortHeader(SftpRemoteSession s, string c) { Session = s; Column = c; } }

    internal enum TransferConflictAction { Overwrite, Skip, Duplicate, Merge, Cancel }
    internal sealed record TransferConflictDecision(TransferConflictAction Action, bool ApplyToAll);
    internal sealed class TransferConflictContext
    {
        public string Name { get; init; } = "";
        public string SourcePath { get; init; } = "";
        public string TargetPath { get; init; } = "";
        public bool IsDirectory { get; init; }
        public bool IsUpload { get; init; }
        public bool AllowMerge { get; init; }
        public bool AllowApplyToAll { get; init; }
    }

    public sealed partial class SftpPage : Page, INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly ConnectionStorage _storage = new();
        private readonly KnownHostsService _knownHosts = new();
        private CancellationTokenSource? _transferCts;
        private SftpRemoteSession? _activeTransferSession;
        private TransferConflictAction? _batchConflictAction;
        private readonly List<ListView> _remoteFileLists = new();
        private readonly List<Grid> _remoteHeaderGrids = new();
        private readonly HashSet<UIElement> _remoteDropTargets = new();
        private string _localSortColumn = "Name";
        private bool _localSortAscending = true;
        private string _localSearchText = "";
        private bool _localShowHiddenFiles;
        public ObservableCollection<SftpFileItemViewModel> LocalItems { get; } = new();
        public SftpTransferProgressViewModel TransferProgress { get; } = new();

        private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public string LocalPath
        {
            get => _localPath;
            set { if (_localPath != value) { _localPath = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalPath))); } }
        }

        public SftpPage()
        {
            InitializeComponent();
            Loaded += (_, _) => PopulateLocalHeader();
            ActualThemeChanged += (_, _) => { RefreshHeaderIndicators(); UpdateSearchBoxBackgrounds(); };
            LocalFileList.ItemTemplate = BuildFileItemTemplate();
            AllowDrop = true;
            DragEnter += Page_DragOver;
            DragOver += Page_DragOver;
            AddHandler(DragEnterEvent, new DragEventHandler(Page_DragOver), true);
            AddHandler(DragOverEvent, new DragEventHandler(Page_DragOver), true);
            AddHandler(DropEvent, new DragEventHandler(async (_, e) =>
            {
                if (IsPointerOverElement(e, RemoteTabView) && GetSelectedRemoteSession() is { } session)
                {
                    await HandleRemoteDropAsync(session, e, GetRemoteDropDirectory(session, e));
                    e.Handled = true;
                }
            }), true);
            RemoteTabView.AllowDrop = true;
            RemoteTabView.DragEnter += RemoteSurface_DragOver;
            RemoteTabView.DragOver += RemoteSurface_DragOver;
            RemoteTabView.AddHandler(DragEnterEvent, new DragEventHandler(RemoteSurface_DragOver), true);
            RemoteTabView.AddHandler(DragOverEvent, new DragEventHandler(RemoteSurface_DragOver), true);
            RemoteTabView.Drop += async (_, e) =>
            {
                var session = GetSelectedRemoteSession();
                if (session != null) await HandleRemoteDropAsync(session, e, GetRemoteDropDirectory(session, e));
                e.Handled = true;
            };
            _ = RefreshLocalAsync();
            UpdateRemoteEmptyState();
        }

        private void PopulateLocalHeader()
        {
            LocalHeaderGrid.Children.Clear();
            AddSortHeaderButton(LocalHeaderGrid, "Name", 1, HorizontalAlignment.Left, "Name");
            AddSortHeaderButton(LocalHeaderGrid, "Kind", 2, HorizontalAlignment.Left, "Kind");
            AddSortHeaderButton(LocalHeaderGrid, "Size", 3, HorizontalAlignment.Right, "Size");
            AddSortHeaderButton(LocalHeaderGrid, "Modified", 4, HorizontalAlignment.Right, "Modified");
            AddSortHeaderDivider(LocalHeaderGrid, 0);
            AddSortHeaderDivider(LocalHeaderGrid, 1);
            AddSortHeaderDivider(LocalHeaderGrid, 2);
            AddSortHeaderDivider(LocalHeaderGrid, 3);
        }

        private async Task RefreshLocalAsync()
        {
            try
            {
                var path = LocalPath;
                var searchText = _localSearchText;
                var showHidden = _localShowHiddenFiles;
                var items = await Task.Run(() =>
                {
                    var result = new List<SftpFileItemViewModel>();
                    var parent = Directory.GetParent(path);
                    if (parent != null) result.Add(new SftpFileItemViewModel { Name = "..", FullPath = parent.FullName, IsDirectory = true, IsParent = true });
                    var dirInfos = Directory.EnumerateDirectories(path).Select(p => new DirectoryInfo(p));
                    if (!showHidden) dirInfos = dirInfos.Where(d => !d.Attributes.HasFlag(System.IO.FileAttributes.Hidden));
                    var fileInfos = Directory.EnumerateFiles(path).Select(p => new FileInfo(p));
                    if (!showHidden) fileInfos = fileInfos.Where(f => !f.Attributes.HasFlag(System.IO.FileAttributes.Hidden));
                    var dirs = dirInfos.Select(d => new SftpFileItemViewModel { Name = d.Name, FullPath = d.FullName, IsDirectory = true, Modified = d.LastWriteTime });
                    var files = fileInfos.Select(f => new SftpFileItemViewModel { Name = f.Name, FullPath = f.FullName, IsDirectory = false, Size = f.Length, Modified = f.LastWriteTime });
                    var all = dirs.Concat(files);
                    if (!string.IsNullOrWhiteSpace(searchText))
                        all = all.Where(i => i.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
                    result.AddRange(SortItems(all, _localSortColumn, _localSortAscending));
                    return result;
                });
                LocalItems.Clear();
                foreach (var item in items) LocalItems.Add(item);
            }
            catch (Exception ex) { ShowStatus("本机目录读取失败", ex.Message, InfoBarSeverity.Error); }
        }

        private async Task RefreshRemoteAsync(SftpRemoteSession session)
        {
            try
            {
                var showHidden = session.ShowHiddenFiles;
                var searchText = session.SearchText;
                var items = await Task.Run(() =>
                {
                    var result = new List<SftpFileItemViewModel>();
                    if (session.RemotePath != "/" && session.RemotePath != ".")
                        result.Add(new SftpFileItemViewModel { Name = "..", FullPath = GetRemoteParent(session.RemotePath), IsDirectory = true, IsParent = true });
                    var all = session.Client.ListDirectory(session.RemotePath).Where(f => f.Name != "." && f.Name != "..").Select(ToRemoteItem);
                    if (!showHidden) all = all.Where(i => !i.Name.StartsWith("."));
                    if (!string.IsNullOrWhiteSpace(searchText)) all = all.Where(i => i.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
                    result.AddRange(SortItems(all, session.SortColumn, session.SortAscending));
                    return result;
                });
                session.Items.Clear();
                foreach (var item in items) session.Items.Add(item);
            }
            catch (Exception ex) { ShowStatus("远程目录读取失败", ex.Message, InfoBarSeverity.Error); }
        }

        private static SftpFileItemViewModel ToRemoteItem(ISftpFile f) => new() { Name = f.Name, FullPath = f.FullName, IsDirectory = f.IsDirectory, Size = f.Length, Modified = f.LastWriteTime };

        private static IEnumerable<SftpFileItemViewModel> SortItems(IEnumerable<SftpFileItemViewModel> items, string col, bool asc) => col switch
        {
            "Kind" => asc ? items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.KindText, StringComparer.OrdinalIgnoreCase) : items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(i => i.KindText, StringComparer.OrdinalIgnoreCase),
            "Size" => asc ? items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Size) : items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(i => i.Size),
            "Modified" => asc ? items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Modified) : items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(i => i.Modified),
            _ => asc ? items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase) : items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };

        private async void SortHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            if (el.Tag is RemoteSortHeader rh) { if (rh.Session.SortColumn == rh.Column) rh.Session.SortAscending = !rh.Session.SortAscending; else { rh.Session.SortColumn = rh.Column; rh.Session.SortAscending = true; } await RefreshRemoteAsync(rh.Session); RefreshHeaderIndicators(); return; }
            if (el.Tag is not string col) return;
            if (_localSortColumn == col) _localSortAscending = !_localSortAscending; else { _localSortColumn = col; _localSortAscending = true; }
            await RefreshLocalAsync(); RefreshHeaderIndicators();
        }

        private void RefreshHeaderIndicators() { PopulateLocalHeader(); foreach (var h in _remoteHeaderGrids) { if (h.Tag is SftpRemoteSession s) RebuildHeaderGrid(h, s); } }

        private static DataTemplate BuildFileItemTemplate()
        {
            const string xaml = @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
    <Grid Padding='8,7' ColumnSpacing='10' Background='Transparent'>
        <Grid.ColumnDefinitions><ColumnDefinition Width='24'/><ColumnDefinition Width='*'/><ColumnDefinition Width='84'/><ColumnDefinition Width='92'/><ColumnDefinition Width='140'/></Grid.ColumnDefinitions>
        <FontIcon Grid.Column='0' Glyph='{Binding IconGlyph}' FontSize='16' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/>
        <TextBlock Grid.Column='1' Text='{Binding Name}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/>
        <TextBlock Grid.Column='2' Text='{Binding KindText}' FontSize='12' Foreground='{ThemeResource TextFillColorSecondaryBrush}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/>
        <TextBlock Grid.Column='3' Text='{Binding SizeText}' FontFamily='Consolas' FontSize='12' Foreground='{ThemeResource TextFillColorSecondaryBrush}' HorizontalAlignment='Right' VerticalAlignment='Center'/>
        <TextBlock Grid.Column='4' Text='{Binding ModifiedText}' FontFamily='Consolas' FontSize='12' Foreground='{ThemeResource TextFillColorSecondaryBrush}' HorizontalAlignment='Right' VerticalAlignment='Center'/>
    </Grid></DataTemplate>";
            return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        }

        private async void LocalPathBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key != Windows.System.VirtualKey.Enter) return; e.Handled = true; var p = LocalPathBox.Text.Trim(); if (Directory.Exists(p)) { LocalPath = Path.GetFullPath(p); await RefreshLocalAsync(); } else ShowStatus("路径不存在", p, InfoBarSeverity.Warning); }
        private async void LocalUpButton_Click(object sender, RoutedEventArgs e) { var p = Directory.GetParent(LocalPath); if (p == null) return; LocalPath = p.FullName; await RefreshLocalAsync(); }
        private async void LocalRefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshLocalAsync();
        private async void LocalSearchBox_TextChanged(object sender, TextChangedEventArgs e) { _localSearchText = LocalSearchBox.Text; await RefreshLocalAsync(); }
        private async void LocalHiddenFilesToggle_Click(object sender, RoutedEventArgs e) { _localShowHiddenFiles = LocalHiddenFilesToggle.IsChecked == true; await RefreshLocalAsync(); }

        private void LocalSearchIcon_Click(object sender, RoutedEventArgs e)
        {
            LocalSearchIconBtn.Visibility = Visibility.Collapsed;
            LocalSearchBox.Visibility = Visibility.Visible;
            AnimateSearchWidth(LocalSearchContainer, 34, 160, 250);
            LocalSearchBox.Focus(FocusState.Programmatic);
        }
        private async void CollapseLocalSearch(object sender, RoutedEventArgs e)
        {
            AnimateSearchWidth(LocalSearchContainer, 160, 34, 150);
            LocalSearchBox.Text = "";
            LocalSearchBox.Visibility = Visibility.Collapsed;
            LocalSearchIconBtn.Visibility = Visibility.Visible;
            if (_localSearchText != "") { _localSearchText = ""; await RefreshLocalAsync(); }
        }
        private void LocalSearchBox_KeyDown2(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; CollapseLocalSearch(sender, e); }
        }
        private void LocalSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(LocalSearchBox.Text)) CollapseLocalSearch(sender, e);
        }

        private void UpdateSearchBoxBackgrounds()
        {
            var bg = GetSearchBoxBackgroundBrush();
            LocalSearchBox.Resources["TextControlBackground"] = bg;
            LocalSearchBox.Resources["TextControlBackgroundFocused"] = bg;
            LocalSearchBox.Resources["TextControlBackgroundPointerOver"] = bg;
            LocalSearchBox.Resources["TextControlBackgroundDisabled"] = bg;
            foreach (var tab in RemoteTabView.TabItems.OfType<TabViewItem>())
            {
                if (tab.Content is Grid root)
                    foreach (var tb in FindVisualDescendants<TextBox>(root))
                    {
                        tb.Resources["TextControlBackground"] = bg;
                        tb.Resources["TextControlBackgroundFocused"] = bg;
                        tb.Resources["TextControlBackgroundPointerOver"] = bg;
                        tb.Resources["TextControlBackgroundDisabled"] = bg;
                    }
            }
        }

        private Brush GetSearchBoxBackgroundBrush()
        {
            var themeKey = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            if (Resources.ThemeDictionaries.TryGetValue(themeKey, out var themeObj) &&
                themeObj is ResourceDictionary themeDict &&
                themeDict.TryGetValue("SearchBoxBackgroundColor", out var val) && val is Windows.UI.Color c)
                return new SolidColorBrush(c);
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private static void AnimateSearchWidth(FrameworkElement target, double from, double to, int ms)
        {
            var anim = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EnableDependentAnimation = true };
            var sb = new Storyboard(); sb.Children.Add(anim);
            Storyboard.SetTarget(anim, target); Storyboard.SetTargetProperty(anim, "Width");
            sb.Begin();
        }
        private async void LocalFileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (LocalFileList.SelectedItem is not SftpFileItemViewModel item || !item.IsDirectory) return; LocalPath = item.FullPath; await RefreshLocalAsync(); }

        private async void LocalFileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            if (e.Items.FirstOrDefault() is not SftpFileItemViewModel item || item.IsParent) { e.Cancel = true; return; }
            e.Data.SetText($"local|{item.FullPath}"); e.Data.RequestedOperation = DataPackageOperation.Copy;
            try { IStorageItem si = Directory.Exists(item.FullPath) ? await StorageFolder.GetFolderFromPathAsync(item.FullPath) : await StorageFile.GetFileFromPathAsync(item.FullPath); e.Data.SetStorageItems(new[] { si }); } catch { }
        }

        private void LocalFileList_DragOver(object sender, DragEventArgs e) { e.AcceptedOperation = DataPackageOperation.Copy; e.Handled = true; }
        private void Page_DragOver(object sender, DragEventArgs e) { if (IsPointerOverElement(e, RemoteTabView)) RemoteSurface_DragOver(sender, e); }
        private void RemoteSurface_DragOver(object sender, DragEventArgs e) { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "上传到服务器"; e.DragUIOverride.IsCaptionVisible = true; e.Handled = true; }

        private async void LocalFileList_Drop(object sender, DragEventArgs e) { var p = await TryGetDragPayloadAsync(e); if (p == null || p.Value.Source != "remote") return; var s = GetSelectedRemoteSession(); if (s == null) return; await DownloadRemoteItemAsync(s, p.Value.Path, LocalPath); }

        private async void RemoteTabView_AddTabButtonClick(TabView sender, object args) => await OpenRemoteSessionFromPickerAsync();
        private async void OpenRemoteButton_Click(object sender, RoutedEventArgs e) => await OpenRemoteSessionFromPickerAsync();
        private void RemoteTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) { if (args.Tab.Tag is SftpRemoteSession s) s.Dispose(); RemoteTabView.TabItems.Remove(args.Tab); UpdateRemoteEmptyState(); }

        public async Task OpenRemoteSessionFromPickerAsync() { var p = await PickConnectionAsync(); if (p != null) await OpenRemoteSessionAsync(p); }

        public async Task OpenRemoteSessionAsync(ConnectionProfile profile)
        {
            SetTransferStatus("正在连接 SFTP", $"{profile.Username}@{profile.Host}:{profile.Port}", 0);
            try
            {
                var client = await Task.Run(() => BuildSftpClient(profile));
                var session = new SftpRemoteSession(profile, client) { RemotePath = client.WorkingDirectory };
                var content = BuildRemoteTabContent(session);
                var tab = new TabViewItem { Header = profile.Name, IconSource = new FontIconSource { Glyph = "\uE8B7", FontSize = 14 }, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Tag = session, VerticalAlignment = VerticalAlignment.Center };
                RemoteTabView.TabItems.Add(tab); RemoteTabView.SelectedItem = tab; UpdateRemoteEmptyState();
                await LoadBookmarksAsync(session); await RefreshRemoteAsync(session); HideTransferStatus();
            }
            catch (Exception ex) { SetTransferStatus("SFTP 连接失败", ex.Message, 0); }
        }

        private async Task<ConnectionProfile?> PickConnectionAsync()
        {
            var profiles = (await _storage.LoadConnectionsAsync()).OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Group).ThenBy(p => p.Name).ToList();
            if (profiles.Count == 0) { ShowStatus("没有可用连接", "请先在连接列表中新建 SSH 连接。", InfoBarSeverity.Warning); return null; }
            var list = new ListView { ItemsSource = profiles, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360, ItemTemplate = BuildProfileTemplate() };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "选择 SFTP 连接", Content = list, PrimaryButtonText = "连接", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            list.DoubleTapped += (_, _) => dialog.Hide();
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary && list.SelectedItem == null) return null;
            return list.SelectedItem as ConnectionProfile;
        }

        private static DataTemplate BuildProfileTemplate()
        {
            const string xaml = @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
    <Grid Padding='8,7' ColumnSpacing='10'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
    <FontIcon Glyph='&#xE8C8;' FontSize='16' Foreground='{ThemeResource AccentTextFillColorPrimaryBrush}'/>
    <StackPanel Grid.Column='1' Spacing='2'><TextBlock Text='{Binding Name}'/><TextBlock FontFamily='Consolas' FontSize='12' Foreground='{ThemeResource TextFillColorSecondaryBrush}'><Run Text='{Binding Username}'/><Run Text='@'/><Run Text='{Binding Host}'/><Run Text=':'/><Run Text='{Binding Port}'/></TextBlock></StackPanel></Grid></DataTemplate>";
            return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        }

        private Grid BuildRemoteTabContent(SftpRemoteSession session)
        {
            var root = new Grid { Padding = new Thickness(0, 8, 0, 0), RowSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            root.AllowDrop = true;
            root.DragEnter += RemoteSurface_DragOver; root.DragOver += RemoteSurface_DragOver;
            root.AddHandler(DragEnterEvent, new DragEventHandler(RemoteSurface_DragOver), true);
            root.AddHandler(DragOverEvent, new DragEventHandler(RemoteSurface_DragOver), true);
            root.AddHandler(DropEvent, new DragEventHandler(async (_, e) => { await HandleRemoteDropAsync(session, e, GetRemoteDropDirectory(session, e)); e.Handled = true; }), true);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock { Text = $"{session.Profile.Username}@{session.Profile.Host}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(title, 0);

            // Expandable search container
            var remoteSearchBox = new TextBox { PlaceholderText = "搜索文件...", FontSize = 13, Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(4, 5, 4, 5) };
            var bg = GetSearchBoxBackgroundBrush();
            remoteSearchBox.Resources["TextControlBackground"] = bg;
            remoteSearchBox.Resources["TextControlBackgroundFocused"] = bg;
            remoteSearchBox.Resources["TextControlBackgroundPointerOver"] = bg;
            remoteSearchBox.Resources["TextControlBackgroundDisabled"] = bg;
            remoteSearchBox.Resources["TextControlBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            remoteSearchBox.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            remoteSearchBox.Resources["TextControlBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            remoteSearchBox.Resources["TextControlBorderBrushDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            remoteSearchBox.TextChanged += async (_, _) => { session.SearchText = remoteSearchBox.Text; await RefreshRemoteAsync(session); };
            var remoteSearchIconBtn = new Button { Width = 34, Height = 34, Padding = new Thickness(0), Content = new FontIcon { Glyph = "\uE721", FontSize = 14 } };
            ToolTipService.SetToolTip(remoteSearchIconBtn, "搜索");
            var remoteSearchContainer = new Grid { Width = 34.0, ColumnSpacing = 0 };
            remoteSearchContainer.Children.Add(remoteSearchBox); remoteSearchContainer.Children.Add(remoteSearchIconBtn);
            remoteSearchIconBtn.Click += (_, _) =>
            {
                remoteSearchIconBtn.Visibility = Visibility.Collapsed;
                remoteSearchBox.Visibility = Visibility.Visible;
                AnimateSearchWidth(remoteSearchContainer, 34, 160, 250);
                remoteSearchBox.Focus(FocusState.Programmatic);
            };
            remoteSearchBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; closeRemoteSearch(); } };
            remoteSearchBox.LostFocus += (_, _) => { if (string.IsNullOrEmpty(remoteSearchBox.Text)) closeRemoteSearch(); };
            void closeRemoteSearch()
            {
                AnimateSearchWidth(remoteSearchContainer, 160, 34, 150);
                remoteSearchBox.Text = ""; remoteSearchBox.Visibility = Visibility.Collapsed;
                remoteSearchIconBtn.Visibility = Visibility.Visible;
                if (session.SearchText != "") { session.SearchText = ""; _ = RefreshRemoteAsync(session); }
            }
            Grid.SetColumn(remoteSearchContainer, 1);

            var remoteHiddenBtn = new ToggleButton { Width = 34, Height = 34, Padding = new Thickness(0), IsChecked = false, Content = new FontIcon { Glyph = "\uED1A", FontSize = 14 } };
            ToolTipService.SetToolTip(remoteHiddenBtn, "显示隐藏文件");
            remoteHiddenBtn.Click += async (_, _) => { session.ShowHiddenFiles = remoteHiddenBtn.IsChecked == true; await RefreshRemoteAsync(session); };
            Grid.SetColumn(remoteHiddenBtn, 2);

            var upBtn = new Button { Width = 34, Height = 34, Padding = new Thickness(0), Content = new FontIcon { Glyph = "\uE74A", FontSize = 14 } };
            ToolTipService.SetToolTip(upBtn, "返回上级");
            upBtn.Click += async (_, _) => { session.RemotePath = GetRemoteParent(session.RemotePath); await RefreshRemoteAsync(session); }; Grid.SetColumn(upBtn, 3);

            var refBtn = new Button { Width = 34, Height = 34, Padding = new Thickness(0), Content = new FontIcon { Glyph = "\uE72C", FontSize = 14 } };
            ToolTipService.SetToolTip(refBtn, "刷新"); refBtn.Click += async (_, _) => await RefreshRemoteAsync(session); Grid.SetColumn(refBtn, 4);

            var bmBtn = new Button { Width = 34, Height = 34, Padding = new Thickness(0), Content = new FontIcon { Glyph = "\uE734", FontSize = 14 } };
            ToolTipService.SetToolTip(bmBtn, "书签"); bmBtn.Click += (_, _) => ShowBookmarkFlyout(bmBtn, session); Grid.SetColumn(bmBtn, 5);

            header.Children.Add(title); header.Children.Add(remoteSearchContainer); header.Children.Add(remoteHiddenBtn); header.Children.Add(upBtn); header.Children.Add(refBtn); header.Children.Add(bmBtn); Grid.SetRow(header, 0);

            var pathBox = new TextBox { FontFamily = new FontFamily("Consolas"), Text = session.RemotePath };
            session.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SftpRemoteSession.RemotePath)) pathBox.Text = session.RemotePath; };
            pathBox.KeyDown += async (_, e) => { if (e.Key != Windows.System.VirtualKey.Enter) return; e.Handled = true; var np = pathBox.Text.Trim(); if (string.IsNullOrEmpty(np)) return; session.RemotePath = np; await RefreshRemoteAsync(session); };
            Grid.SetRow(pathBox, 1);

            var fileHeader = BuildFileHeaderGrid(session); Grid.SetRow(fileHeader, 2); _remoteHeaderGrids.Add(fileHeader);

            var list = new ListView { ItemsSource = session.Items, ItemTemplate = BuildFileItemTemplate(), SelectionMode = ListViewSelectionMode.Single, CanDragItems = true, AllowDrop = true, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Tag = session };
            var blankFooter = new Border { HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            RegisterRemoteDropTarget(blankFooter, session); list.Footer = blankFooter; _remoteFileLists.Add(list);
            list.ItemContainerStyle = (Style)LocalFileList.ItemContainerStyle;
            list.RightTapped += (_, e) => { var it = FindDataContext<SftpFileItemViewModel>(e.OriginalSource as DependencyObject); if (it != null && !it.IsParent) { list.SelectedItem = it; BuildRemoteItemFlyout(session, it).ShowAt(list, e.GetPosition(list)); } else BuildRemoteBlankFlyout(session).ShowAt(list, e.GetPosition(list)); e.Handled = true; };
            list.DoubleTapped += async (_, _) => { if (list.SelectedItem is not SftpFileItemViewModel it || !it.IsDirectory) return; session.RemotePath = it.FullPath; await RefreshRemoteAsync(session); };
            list.DragItemsStarting += (_, e) => { if (e.Items.FirstOrDefault() is not SftpFileItemViewModel it || it.IsParent) { e.Cancel = true; return; } e.Data.SetText($"remote|{it.FullPath}"); e.Data.RequestedOperation = DataPackageOperation.Copy; };
            list.DragEnter += RemoteSurface_DragOver; list.DragOver += RemoteSurface_DragOver;
            list.AddHandler(DragEnterEvent, new DragEventHandler(RemoteSurface_DragOver), true);
            list.AddHandler(DragOverEvent, new DragEventHandler(RemoteSurface_DragOver), true);
            list.AddHandler(DropEvent, new DragEventHandler(async (_, e) => { await HandleRemoteDropAsync(session, e, GetRemoteDropDirectory(session, e)); e.Handled = true; }), true);
            list.Loaded += (_, _) => { RegisterRemoteListDropTargets(list, session); DispatcherQueue.TryEnqueue(() => RegisterRemoteListDropTargets(list, session)); };

            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            RegisterRemoteDropTarget(host, session);
            list.Loaded += (_, _) => QueueRemoteBlankFooterUpdate(list, blankFooter, host);
            host.SizeChanged += (_, _) => QueueRemoteBlankFooterUpdate(list, blankFooter, host);
            session.Items.CollectionChanged += (_, _) => QueueRemoteBlankFooterUpdate(list, blankFooter, host);
            var backplate = new Border { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            RegisterRemoteDropTarget(backplate, session); host.Children.Add(backplate); host.Children.Add(list); Grid.SetRow(host, 3);

            root.Children.Add(header); root.Children.Add(pathBox); root.Children.Add(fileHeader); root.Children.Add(host);
            return root;
        }

        private Grid BuildFileHeaderGrid(SftpRemoteSession session)
        {
            var g = new Grid { Tag = session };
            // Apply the same XAML-defined style used by LocalHeaderGrid.
            // {ThemeResource} brushes inside the style resolve in this element's
            // actual theme context, so dark/light mode renders correctly even
            // when hosted inside a TabView.
            if (Resources.TryGetValue("FileHeaderGridStyle", out var styleObj) && styleObj is Style s)
                g.Style = s;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            RebuildHeaderGrid(g, session);
            return g;
        }

        private void RebuildHeaderGrid(Grid g, SftpRemoteSession s)
        {
            g.Children.Clear();
            AddSortHeaderButton(g, "Name", 1, HorizontalAlignment.Left, new RemoteSortHeader(s, "Name"));
            AddSortHeaderButton(g, "Kind", 2, HorizontalAlignment.Left, new RemoteSortHeader(s, "Kind"));
            AddSortHeaderButton(g, "Size", 3, HorizontalAlignment.Right, new RemoteSortHeader(s, "Size"));
            AddSortHeaderButton(g, "Modified", 4, HorizontalAlignment.Right, new RemoteSortHeader(s, "Modified"));
            AddSortHeaderDivider(g, 0);
            AddSortHeaderDivider(g, 1);
            AddSortHeaderDivider(g, 2);
            AddSortHeaderDivider(g, 3);
        }

        /// <summary>
        /// Resolves a WinUI 3 theme resource brush using the page's <see cref="ActualTheme"/>,
        /// so the correct dark/light value is returned regardless of which theme context the
        /// calling element lives in (e.g. inside a TabView that may override the app theme).
        /// The header grids are fully rebuilt on ActualThemeChanged, so this only needs to be
        /// correct at call time.
        /// </summary>
        private Brush GetThemeBrush(string resourceKey)
        {
            var themeKey = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            // Try page-level theme dictionaries first, then application-level
            var dicts = new[]
            {
                Resources.ThemeDictionaries,
                Application.Current.Resources.ThemeDictionaries
            };
            foreach (var allDicts in dicts)
            {
                if (allDicts.TryGetValue(themeKey, out var themeObj) &&
                    themeObj is ResourceDictionary themeDict &&
                    themeDict.TryGetValue(resourceKey, out var val) && val is Brush b)
                    return b;
            }
            // Last-resort fallback (application-level, same as before)
            return Application.Current.Resources[resourceKey] as Brush
                   ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private void AddSortHeaderButton(Grid grid, string text, int column, HorizontalAlignment alignment, object tag)
        {
            var sg = GetHeaderSortGlyph(text, tag);
            var isActive = sg != null;
            var primaryFg = GetThemeBrush("TextFillColorPrimaryBrush");

            // Build inner content: text + optional sort glyph
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = alignment
            };

            var txt = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = primaryFg,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(txt);

            if (isActive)
            {
                var icon = new FontIcon
                {
                    Glyph = sg,
                    FontSize = 10,
                    Foreground = primaryFg,
                    VerticalAlignment = VerticalAlignment.Center
                };
                content.Children.Add(icon);
            }

            // Build the button with subtle hover feedback
            // Padding="0" so text aligns exactly with body cell content (same column grid)
            // Margin right=6px gives visual breathing room from the column divider
            var btn = new Button
            {
                Tag = tag,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                MinHeight = 0,
                MinWidth = 0,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = alignment,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = content,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };

            // Theme-aware hover/press resources
            var subtleHover = GetThemeBrush("SubtleFillColorSecondaryBrush");
            var subtlePress = GetThemeBrush("SubtleFillColorTertiaryBrush");

            btn.Resources["ButtonBackgroundPointerOver"] = subtleHover;
            btn.Resources["ButtonBackgroundPressed"] = subtlePress;
            btn.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            btn.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            btn.Resources["ButtonForegroundPointerOver"] = primaryFg;
            btn.Resources["ButtonForegroundPressed"] = primaryFg;

            btn.Click += SortHeader_Click;
            Grid.SetColumn(btn, column);
            grid.Children.Add(btn);
        }

        private string? GetHeaderSortGlyph(string column, object tag)
        {
            string? sc; bool asc;
            if (tag is RemoteSortHeader rh) { sc = rh.Session.SortColumn; asc = rh.Session.SortAscending; }
            else { sc = _localSortColumn; asc = _localSortAscending; }
            return sc == column ? (asc ? "\uE70E" : "\uE70D") : null;
        }

        private void AddSortHeaderDivider(Grid grid, int column)
        {
            var d = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 6, 0, 6),
                Background = GetThemeBrush("DividerStrokeColorDefaultBrush"),
                IsHitTestVisible = false
            };
            Grid.SetColumn(d, column);
            grid.Children.Add(d);
        }

        private MenuFlyout BuildRemoteItemFlyout(SftpRemoteSession session, SftpFileItemViewModel item)
        {
            var f = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = "重命名" }; rename.Icon = new FontIcon { Glyph = "\uE70F" }; rename.Click += async (_, _) => await RenameRemoteItemAsync(session, item); f.Items.Add(rename);
            var chmod = new MenuFlyoutItem { Text = "修改权限" }; chmod.Icon = new FontIcon { Glyph = "\uE72E" }; chmod.Click += async (_, _) => await ChangeRemotePermissionsAsync(session, item); f.Items.Add(chmod);
            return f;
        }

        private MenuFlyout BuildRemoteBlankFlyout(SftpRemoteSession session)
        {
            var f = new MenuFlyout();
            var nf = new MenuFlyoutItem { Text = "新建文件" }; nf.Icon = new FontIcon { Glyph = "\uE8A5" }; nf.Click += async (_, _) => await CreateRemoteFileAsync(session); f.Items.Add(nf);
            var nd = new MenuFlyoutItem { Text = "新建文件夹" }; nd.Icon = new FontIcon { Glyph = "\uE8B7" }; nd.Click += async (_, _) => await CreateRemoteDirectoryAsync(session); f.Items.Add(nd);
            return f;
        }

        private async Task RenameRemoteItemAsync(SftpRemoteSession session, SftpFileItemViewModel item)
        {
            var input = new TextBox { Text = item.Name, Header = "名称", SelectionStart = 0, SelectionLength = item.Name.Length };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "重命名", Content = input, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var nn = input.Text.Trim(); if (string.IsNullOrEmpty(nn) || nn == item.Name) return;
            try { var np = CombineRemote(GetRemoteParent(item.FullPath), nn); await Task.Run(() => session.Client.RenameFile(item.FullPath, np, true)); await RefreshRemoteAsync(session); }
            catch (Exception ex) { SetTransferStatus("重命名失败", ex.Message, 0); }
        }

        private async Task ChangeRemotePermissionsAsync(SftpRemoteSession session, SftpFileItemViewModel item)
        {
            var input = new TextBox { Header = "权限", Text = item.IsDirectory ? "755" : "644", PlaceholderText = "例如 644 或 755", FontFamily = new FontFamily("Consolas") };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "修改权限", Content = input, PrimaryButtonText = "应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (s, a) => { if (!TryParseOctalPermissions(input.Text, out _)) a.Cancel = true; };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (!TryParseOctalPermissions(input.Text, out var mode)) return;
            try { await Task.Run(() => session.Client.ChangePermissions(item.FullPath, mode)); await RefreshRemoteAsync(session); }
            catch (Exception ex) { SetTransferStatus("修改权限失败", ex.Message, 0); }
        }

        private async Task CreateRemoteFileAsync(SftpRemoteSession session)
        {
            var name = await PromptForNameAsync("新建文件", "文件名", "new-file.txt"); if (string.IsNullOrWhiteSpace(name)) return;
            try { var rp = CombineRemote(session.RemotePath, name.Trim()); await Task.Run(() => { using var s = session.Client.Create(rp); }); await RefreshRemoteAsync(session); }
            catch (Exception ex) { SetTransferStatus("新建文件失败", ex.Message, 0); }
        }

        private async Task CreateRemoteDirectoryAsync(SftpRemoteSession session)
        {
            var name = await PromptForNameAsync("新建文件夹", "文件夹名", "New Folder"); if (string.IsNullOrWhiteSpace(name)) return;
            try { var rp = CombineRemote(session.RemotePath, name.Trim()); await Task.Run(() => session.Client.CreateDirectory(rp)); await RefreshRemoteAsync(session); }
            catch (Exception ex) { SetTransferStatus("新建文件夹失败", ex.Message, 0); }
        }

        private async Task<string?> PromptForNameAsync(string title, string header, string def)
        {
            var input = new TextBox { Header = header, Text = def, SelectionStart = 0, SelectionLength = def.Length };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = title, Content = input, PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
        }

        private static bool TryParseOctalPermissions(string text, out short mode) { mode = 0; var t = text.Trim(); if (t.Length is < 3 or > 4 || t.Any(c => c < '0' || c > '7')) return false; mode = Convert.ToInt16(t, 8); return true; }
        private static T? FindDataContext<T>(DependencyObject? source) { while (source != null) { if (source is FrameworkElement fe && fe.DataContext is T v) return v; source = VisualTreeHelper.GetParent(source); } return default; }
        private void RegisterRemoteListDropTargets(ListView list, SftpRemoteSession session) { foreach (var t in FindVisualDescendants<UIElement>(list)) RegisterRemoteDropTarget(t, session); }
        private void RegisterRemoteDropTarget(UIElement target, SftpRemoteSession session)
        {
            if (!_remoteDropTargets.Add(target)) return;
            target.AllowDrop = true; target.DragEnter += RemoteSurface_DragOver; target.DragOver += RemoteSurface_DragOver;
            target.AddHandler(DragEnterEvent, new DragEventHandler(RemoteSurface_DragOver), true);
            target.AddHandler(DragOverEvent, new DragEventHandler(RemoteSurface_DragOver), true);
            target.AddHandler(DropEvent, new DragEventHandler(async (_, e) => { await HandleRemoteDropAsync(session, e, GetRemoteDropDirectory(session, e)); e.Handled = true; }), true);
        }
        private void QueueRemoteBlankFooterUpdate(ListView list, Border footer, FrameworkElement host) { DispatcherQueue.TryEnqueue(() => { UpdateRemoteBlankFooterHeight(list, footer, host); DispatcherQueue.TryEnqueue(() => UpdateRemoteBlankFooterHeight(list, footer, host)); }); }
        private static void UpdateRemoteBlankFooterHeight(ListView list, Border footer, FrameworkElement host) { if (host.ActualHeight <= 0) { footer.MinHeight = 0; return; } var ih = 0d; foreach (var i in list.Items) ih += list.ContainerFromItem(i) is FrameworkElement c && c.ActualHeight > 0 ? c.ActualHeight : 42; footer.MinHeight = Math.Max(0, host.ActualHeight - ih - 2); }
        private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject { var c = VisualTreeHelper.GetChildrenCount(root); for (var i = 0; i < c; i++) { var ch = VisualTreeHelper.GetChild(root, i); if (ch is T v) yield return v; foreach (var d in FindVisualDescendants<T>(ch)) yield return d; } }

        private SftpClient BuildSftpClient(ConnectionProfile profile)
        {
            SftpClient client; NamedPipeClientStream? agentPipe = null;
            try
            {
                if (profile.AuthType == "PrivateKey") { var pp = ConnectionStorage.DecryptSecret(profile.EncryptedPassphrase); var kf = string.IsNullOrEmpty(pp) ? new PrivateKeyFile(profile.PrivateKeyPath) : new PrivateKeyFile(profile.PrivateKeyPath, pp); client = new SftpClient(profile.Host, profile.Port, profile.Username, kf); }
                else if (profile.AuthType == "Agent") { agentPipe = SshAgentService.OpenAgentPipe(3000); var ids = SshAgentService.RequestIdentities(agentPipe); if (ids.Count == 0) throw new InvalidOperationException("SSH Agent 中没有可用的密钥。"); var ks = ids.Select(id => new AgentKeySource(id, agentPipe)).ToArray<Renci.SshNet.IPrivateKeySource>(); var auth = new PrivateKeyAuthenticationMethod(profile.Username, ks); var ci = new ConnectionInfo(profile.Host, profile.Port, profile.Username, auth); client = new SftpClient(ci); }
                else { var pw = ConnectionStorage.DecryptSecret(profile.EncryptedPassword); client = new SftpClient(profile.Host, profile.Port, profile.Username, pw); }
                client.HostKeyReceived += (_, e) => { var fp = BitConverter.ToString(e.FingerPrint).Replace("-", ":"); var t = _knownHosts.Check(profile.Host, profile.Port, e.HostKeyName, fp); if (t == true) e.CanTrust = true; else if (t == false) e.CanTrust = ShowChangedHostKeyDialogAsync(profile.Host, profile.Port, e.HostKeyName, fp).GetAwaiter().GetResult(); else e.CanTrust = ShowNewHostKeyDialogAsync(profile.Host, profile.Port, e.HostKeyName, fp).GetAwaiter().GetResult(); };
                if (profile.KeepAliveIntervalSeconds > 0) client.KeepAliveInterval = TimeSpan.FromSeconds(profile.KeepAliveIntervalSeconds);
                client.Connect(); return client;
            }
            finally { agentPipe?.Dispose(); }
        }

        private async Task<TransferConflictDecision> ShowTransferConflictDialogAsync(TransferConflictContext ctx)
        {
            var tcs = new TaskCompletionSource<TransferConflictDecision>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                var at = new CheckBox { Content = "Apply to All", Visibility = ctx.AllowApplyToAll ? Visibility.Visible : Visibility.Collapsed };
                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
                ContentDialog? dlg = null;
                void AddBtn(string text, TransferConflictAction action) { var b = new Button { Content = text }; b.Click += (_, _) => { tcs.TrySetResult(new TransferConflictDecision(action, at.IsChecked == true)); dlg?.Hide(); }; btns.Children.Add(b); }
                AddBtn("Overwrite", TransferConflictAction.Overwrite); AddBtn("Skip", TransferConflictAction.Skip); AddBtn("Duplicate", TransferConflictAction.Duplicate);
                if (ctx.AllowMerge) AddBtn("Merge", TransferConflictAction.Merge); AddBtn("Cancel", TransferConflictAction.Cancel);
                var side = ctx.IsUpload ? "remote" : "local";
                var panel = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = $"A {side} {(ctx.IsDirectory ? "folder" : "file")} named \"{ctx.Name}\" already exists.", TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = ctx.TargetPath, FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap }, at, btns } };
                dlg = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "File already exists", Content = panel, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
                var r = await dlg.ShowAsync(); if (r == ContentDialogResult.None) tcs.TrySetResult(new TransferConflictDecision(TransferConflictAction.Cancel, false));
            });
            return await tcs.Task;
        }

        private async Task UploadLocalItemAsync(SftpRemoteSession session, string localPath, string remoteDirectory, bool allowApplyToAll = false)
        {
            if (_transferCts != null) return; _transferCts = new CancellationTokenSource(); _activeTransferSession = session; var token = _transferCts.Token;
            try
            {
                var totalBytes = GetLocalTransferSize(localPath); var transferred = 0L; TransferConflictAction? applyToAllAction = null;
                TransferConflictAction resolveConflict(TransferConflictContext ctx) { if (allowApplyToAll && _batchConflictAction is { } ba) return ba; if (applyToAllAction is { } a) return a; var d = ShowTransferConflictDialogAsync(ctx).GetAwaiter().GetResult(); if (d.ApplyToAll) { applyToAllAction = d.Action; if (allowApplyToAll) _batchConflictAction = d.Action; } return d.Action; }
                SetTransferStatus("正在上传", Path.GetFileName(localPath), 0, isCancelable: true);
                await Task.Run(() => { void report(long b) { token.ThrowIfCancellationRequested(); transferred += b; UpdateTransferProgress("正在上传", Path.GetFileName(localPath), transferred, totalBytes); } if (Directory.Exists(localPath)) UploadDirectory(session.Client, localPath, CombineRemote(remoteDirectory, Path.GetFileName(localPath)), report, token, resolveConflict, allowApplyToAll, true); else UploadFile(session.Client, localPath, CombineRemote(remoteDirectory, Path.GetFileName(localPath)), report, token, resolveConflict, allowApplyToAll); }, token);
                await RefreshRemoteAsync(session); CompleteTransferStatus("上传完成", Path.GetFileName(localPath));
            }
            catch (OperationCanceledException) { SetTransferStatus("上传已取消", Path.GetFileName(localPath), 0); _ = HideTransferStatusAfterDelayAsync(); }
            catch (Exception ex) { if (token.IsCancellationRequested) { SetTransferStatus("上传已取消", Path.GetFileName(localPath), 0); _ = HideTransferStatusAfterDelayAsync(); return; } SetTransferStatus("上传失败", ex.Message, 0); }
            finally { _transferCts?.Dispose(); _transferCts = null; _activeTransferSession = null; }
        }

        private async Task DownloadRemoteItemAsync(SftpRemoteSession session, string remotePath, string localDirectory, bool allowApplyToAll = false)
        {
            if (_transferCts != null) return; _transferCts = new CancellationTokenSource(); _activeTransferSession = session; var token = _transferCts.Token;
            try
            {
                var totalBytes = await Task.Run(() => GetRemoteTransferSize(session.Client, remotePath)); var transferred = 0L; TransferConflictAction? applyToAllAction = null;
                TransferConflictAction resolveConflict(TransferConflictContext ctx) { if (allowApplyToAll && _batchConflictAction is { } ba) return ba; if (applyToAllAction is { } a) return a; var d = ShowTransferConflictDialogAsync(ctx).GetAwaiter().GetResult(); if (d.ApplyToAll) { applyToAllAction = d.Action; if (allowApplyToAll) _batchConflictAction = d.Action; } return d.Action; }
                SetTransferStatus("正在下载", GetRemoteName(remotePath), 0, isCancelable: true);
                await Task.Run(() => { void report(long b) { token.ThrowIfCancellationRequested(); transferred += b; UpdateTransferProgress("正在下载", GetRemoteName(remotePath), transferred, totalBytes); } var attrs = session.Client.GetAttributes(remotePath); var target = Path.Combine(localDirectory, GetRemoteName(remotePath)); if (attrs.IsDirectory) DownloadDirectory(session.Client, remotePath, target, report, token, resolveConflict, allowApplyToAll, true); else DownloadFile(session.Client, remotePath, target, report, token, resolveConflict, allowApplyToAll); }, token);
                await RefreshLocalAsync(); CompleteTransferStatus("下载完成", GetRemoteName(remotePath));
            }
            catch (OperationCanceledException) { SetTransferStatus("下载已取消", GetRemoteName(remotePath), 0); _ = HideTransferStatusAfterDelayAsync(); }
            catch (Exception ex) { if (token.IsCancellationRequested) { SetTransferStatus("下载已取消", GetRemoteName(remotePath), 0); _ = HideTransferStatusAfterDelayAsync(); return; } SetTransferStatus("下载失败", ex.Message, 0); }
            finally { _transferCts?.Dispose(); _transferCts = null; _activeTransferSession = null; }
        }

        private static void UploadDirectory(SftpClient client, string localDir, string remoteDir, Action<long> report, CancellationToken token, Func<TransferConflictContext, TransferConflictAction> resolve, bool applyAll, bool isRoot = false)
        {
            token.ThrowIfCancellationRequested();
            if (client.Exists(remoteDir)) { var a = resolve(new TransferConflictContext { Name = Path.GetFileName(localDir), SourcePath = localDir, TargetPath = remoteDir, IsDirectory = true, IsUpload = true, AllowMerge = true, AllowApplyToAll = applyAll }); switch (a) { case TransferConflictAction.Skip: return; case TransferConflictAction.Cancel: throw new OperationCanceledException(token); case TransferConflictAction.Duplicate: remoteDir = GetUniqueRemotePath(client, remoteDir, true); break; case TransferConflictAction.Overwrite: DeleteRemoteDirectoryRecursive(client, remoteDir); break; case TransferConflictAction.Merge: break; } }
            EnsureRemoteDirectory(client, remoteDir);
            foreach (var f in Directory.EnumerateFiles(localDir)) UploadFile(client, f, CombineRemote(remoteDir, Path.GetFileName(f)), report, token, resolve, applyAll);
            foreach (var d in Directory.EnumerateDirectories(localDir)) UploadDirectory(client, d, CombineRemote(remoteDir, Path.GetFileName(d)), report, token, resolve, applyAll);
        }

        private static void UploadFile(SftpClient client, string localFile, string remoteFile, Action<long> report, CancellationToken token, Func<TransferConflictContext, TransferConflictAction> resolve, bool applyAll)
        {
            token.ThrowIfCancellationRequested();
            if (client.Exists(remoteFile)) { var a = resolve(new TransferConflictContext { Name = Path.GetFileName(localFile), SourcePath = localFile, TargetPath = remoteFile, IsDirectory = false, IsUpload = true, AllowMerge = false, AllowApplyToAll = applyAll }); switch (a) { case TransferConflictAction.Skip: return; case TransferConflictAction.Cancel: throw new OperationCanceledException(token); case TransferConflictAction.Duplicate: remoteFile = GetUniqueRemotePath(client, remoteFile, false); break; } }
            using var s = File.OpenRead(localFile); ulong last = 0;
            client.UploadFile(s, remoteFile, true, up => { token.ThrowIfCancellationRequested(); var d = (long)(up - last); last = up; if (d > 0) report(d); });
        }

        private static void DownloadDirectory(SftpClient client, string remoteDir, string localDir, Action<long> report, CancellationToken token, Func<TransferConflictContext, TransferConflictAction> resolve, bool applyAll, bool isRoot = false)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(localDir)) { var a = resolve(new TransferConflictContext { Name = GetRemoteName(remoteDir), SourcePath = remoteDir, TargetPath = localDir, IsDirectory = true, IsUpload = false, AllowMerge = true, AllowApplyToAll = applyAll }); switch (a) { case TransferConflictAction.Skip: return; case TransferConflictAction.Cancel: throw new OperationCanceledException(token); case TransferConflictAction.Duplicate: localDir = GetUniqueLocalPath(localDir, true); break; case TransferConflictAction.Overwrite: Directory.Delete(localDir, true); break; case TransferConflictAction.Merge: break; } }
            Directory.CreateDirectory(localDir);
            foreach (var e in client.ListDirectory(remoteDir).Where(f => f.Name != "." && f.Name != "..")) { var lp = Path.Combine(localDir, e.Name); if (e.IsDirectory) DownloadDirectory(client, e.FullName, lp, report, token, resolve, applyAll); else DownloadFile(client, e.FullName, lp, report, token, resolve, applyAll); }
        }

        private static void DownloadFile(SftpClient client, string remoteFile, string localFile, Action<long> report, CancellationToken token, Func<TransferConflictContext, TransferConflictAction> resolve, bool applyAll)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(localFile)) { var a = resolve(new TransferConflictContext { Name = GetRemoteName(remoteFile), SourcePath = remoteFile, TargetPath = localFile, IsDirectory = false, IsUpload = false, AllowMerge = false, AllowApplyToAll = applyAll }); switch (a) { case TransferConflictAction.Skip: return; case TransferConflictAction.Cancel: throw new OperationCanceledException(token); case TransferConflictAction.Duplicate: localFile = GetUniqueLocalPath(localFile, false); break; } }
            Directory.CreateDirectory(Path.GetDirectoryName(localFile)!); using var s = File.Create(localFile); ulong last = 0;
            client.DownloadFile(remoteFile, s, dl => { token.ThrowIfCancellationRequested(); var d = (long)(dl - last); last = dl; if (d > 0) report(d); });
        }

        private static long GetLocalTransferSize(string path) { if (File.Exists(path)) return new FileInfo(path).Length; if (!Directory.Exists(path)) return 0; return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f).Length).Sum(); }
        private static long GetRemoteTransferSize(SftpClient client, string rp) { var a = client.GetAttributes(rp); if (!a.IsDirectory) return a.Size; long t = 0; foreach (var e in client.ListDirectory(rp).Where(f => f.Name != "." && f.Name != "..")) t += e.IsDirectory ? GetRemoteTransferSize(client, e.FullName) : e.Length; return t; }
        private static string GetUniqueLocalPath(string path, bool isDir) { var d = Path.GetDirectoryName(path) ?? ""; var n = Path.GetFileNameWithoutExtension(path); var ext = isDir ? "" : Path.GetExtension(path); for (var i = 1; ; i++) { var c = Path.Combine(d, $"{n} ({i}){ext}"); if (!File.Exists(c) && !Directory.Exists(c)) return c; } }
        private static string GetUniqueRemotePath(SftpClient client, string rp, bool isDir) { var p = GetRemoteParent(rp); var n = GetRemoteName(rp); var ext = isDir ? "" : Path.GetExtension(n); var stem = isDir ? n : Path.GetFileNameWithoutExtension(n); for (var i = 1; ; i++) { var c = CombineRemote(p, $"{stem} ({i}){ext}"); if (!client.Exists(c)) return c; } }
        private static void DeleteRemoteDirectoryRecursive(SftpClient client, string rd) { foreach (var e in client.ListDirectory(rd).Where(f => f.Name != "." && f.Name != "..")) { if (e.IsDirectory) DeleteRemoteDirectoryRecursive(client, e.FullName); else client.DeleteFile(e.FullName); } client.DeleteDirectory(rd); }
        private static void EnsureRemoteDirectory(SftpClient client, string rd) { var parts = rd.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries); var p = rd.StartsWith('/') ? "/" : "."; foreach (var part in parts) { p = CombineRemote(p, part); if (!client.Exists(p)) client.CreateDirectory(p); } }
        private static async Task<(string Source, string Path)?> TryGetDragPayloadAsync(DragEventArgs e) { if (!e.DataView.Contains(StandardDataFormats.Text)) return null; var t = await e.DataView.GetTextAsync(); var i = t.IndexOf('|'); if (i <= 0) return null; return (t[..i], t[(i + 1)..]); }
        private static string GetRemoteDropDirectory(SftpRemoteSession session, DragEventArgs e) { var it = FindDataContext<SftpFileItemViewModel>(e.OriginalSource as DependencyObject); return it is { IsDirectory: true, IsParent: false } ? it.FullPath : session.RemotePath; }
        private static bool IsPointerOverElement(DragEventArgs e, FrameworkElement el) { if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return false; var p = e.GetPosition(el); return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight; }

        private async Task HandleRemoteDropAsync(SftpRemoteSession session, DragEventArgs e, string remoteDirectory)
        {
            var p = await TryGetDragPayloadAsync(e); if (p != null && p.Value.Source == "local") { await UploadLocalItemAsync(session, p.Value.Path, remoteDirectory); return; }
            var paths = await GetDroppedLocalPathsAsync(e); var aaa = paths.Count > 1; _batchConflictAction = null;
            try { foreach (var lp in paths) await UploadLocalItemAsync(session, lp, remoteDirectory, aaa); } finally { _batchConflictAction = null; }
        }

        private static async Task<IReadOnlyList<string>> GetDroppedLocalPathsAsync(DragEventArgs e)
        {
            var paths = new List<string>();
            if (e.DataView.Contains(StandardDataFormats.StorageItems)) { var si = await e.DataView.GetStorageItemsAsync(); foreach (var i in si) if (i is IStorageItem item && IsExistingLocalPath(item.Path)) paths.Add(item.Path); }
            if (e.DataView.Contains(StandardDataFormats.Text)) { var t = await e.DataView.GetTextAsync(); foreach (var p in ParseDroppedLocalPaths(t)) if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p); }
            return paths;
        }

        private static IEnumerable<string> ParseDroppedLocalPaths(string text) { foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) { var c = raw.Trim().Trim('"'); if (Uri.TryCreate(c, UriKind.Absolute, out var uri) && uri.IsFile) c = uri.LocalPath; if (IsExistingLocalPath(c)) yield return c; } }
        private static bool IsExistingLocalPath(string? path) => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));
        private SftpRemoteSession? GetSelectedRemoteSession() => (RemoteTabView.SelectedItem as TabViewItem)?.Tag as SftpRemoteSession;
        private void UpdateRemoteEmptyState() { RemoteEmptyState.Visibility = RemoteTabView.TabItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
        private void ShowStatus(string title, string message, InfoBarSeverity severity) => SetTransferStatus(title, message, severity == InfoBarSeverity.Success ? 100 : 0);
        private void SetTransferStatus(string title, string detail, double percent, bool isCancelable = false) { TransferProgress.Title = title; TransferProgress.Detail = detail; TransferProgress.Percent = percent; TransferProgress.IsCancelable = isCancelable; TransferPanel.Visibility = Visibility.Visible; }
        private void UpdateTransferProgress(string title, string detail, long transferred, long totalBytes) { var p = totalBytes <= 0 ? 0 : transferred * 100d / totalBytes; DispatcherQueue.TryEnqueue(() => SetTransferStatus(title, detail, p)); }
        private void CompleteTransferStatus(string title, string detail) { SetTransferStatus(title, detail, 100); _ = HideTransferStatusAfterDelayAsync(); }
        private void HideTransferStatus() { TransferProgress.IsCancelable = false; TransferPanel.Visibility = Visibility.Collapsed; }
        private async Task HideTransferStatusAfterDelayAsync() { await Task.Delay(1200); if (_transferCts == null) HideTransferStatus(); }
        private void CancelTransferButton_Click(object sender, RoutedEventArgs e) { _transferCts?.Cancel(); try { _activeTransferSession?.Client.Disconnect(); } catch { } }
        public void Dispose() { foreach (var tab in RemoteTabView.TabItems.OfType<TabViewItem>().ToList()) { if (tab.Tag is SftpRemoteSession s) s.Dispose(); } RemoteTabView.TabItems.Clear(); }

        // ── Bookmark ──────────────────────────────────────────────────────────

        private async void ShowBookmarkFlyout(Button target, SftpRemoteSession session)
        {
            await LoadBookmarksAsync(session);
            var flyout = new Flyout(); var panel = new StackPanel { Spacing = 8, MinWidth = 260, MaxWidth = 360 };
            var addBtn = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };
            var addC = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; addC.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 13, VerticalAlignment = VerticalAlignment.Center }); addC.Children.Add(new TextBlock { Text = "收藏当前路径", VerticalAlignment = VerticalAlignment.Center }); addBtn.Content = addC;
            addBtn.Click += async (_, _) => { flyout.Hide(); await AddBookmarkAsync(session); }; panel.Children.Add(addBtn);
            panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Margin = new Thickness(0, 4, 0, 4) });
            if (session.Bookmarks.Count == 0) { panel.Children.Add(new TextBlock { Text = "暂无收藏路径", FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) }); }
            else { foreach (var bm in session.Bookmarks.OrderByDescending(b => b.AddedAt)) { var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(0, 2, 0, 2) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); var icon = new FontIcon { Glyph = "\uE8B7", FontSize = 13, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"], VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(icon, 0); var ts = new StackPanel { Spacing = 1 }; ts.Children.Add(new TextBlock { Text = bm.DisplayLabel, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis }); ts.Children.Add(new TextBlock { Text = bm.Path, FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextTrimming = TextTrimming.CharacterEllipsis }); Grid.SetColumn(ts, 1); var rmBtn = new Button { Width = 28, Height = 28, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, Tag = bm, VerticalAlignment = VerticalAlignment.Center }; ToolTipService.SetToolTip(rmBtn, "移除收藏"); rmBtn.Click += async (_, _) => { flyout.Hide(); await RemoveBookmarkAsync(session, bm); }; Grid.SetColumn(rmBtn, 2); row.Children.Add(icon); row.Children.Add(ts); row.Children.Add(rmBtn); var clickBorder = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(4), Child = row, Tag = bm }; clickBorder.PointerEntered += (_, _) => clickBorder.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]; clickBorder.PointerExited += (_, _) => clickBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); clickBorder.PointerPressed += async (_, _) => { flyout.Hide(); session.RemotePath = bm.Path; await RefreshRemoteAsync(session); }; panel.Children.Add(clickBorder); } }
            flyout.Content = panel; flyout.ShowAt(target);
        }

        private async Task AddBookmarkAsync(SftpRemoteSession session)
        {
            var cp = session.RemotePath; if (string.IsNullOrWhiteSpace(cp)) return;
            if (session.Bookmarks.Any(b => b.Path == cp)) { ShowStatus("已收藏", "此路径已在收藏列表中", InfoBarSeverity.Informational); return; }
            var ni = new TextBox { Text = GetRemoteName(cp), Header = "名称（可选）", PlaceholderText = "留空则使用路径名称" };
            var dlg = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "收藏路径", Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = cp, FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap }, ni } }, PrimaryButtonText = "添加", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            dlg.Opened += (_, _) => ni.Focus(FocusState.Programmatic);
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            var bm = new SftpBookmark { Path = cp, Name = ni.Text.Trim(), AddedAt = DateTime.Now }; session.Bookmarks.Add(bm); await SaveBookmarksAsync(session); ShowStatus("已收藏", $"路径 {bm.DisplayLabel} 已添加到书签", InfoBarSeverity.Success);
        }

        private async Task RemoveBookmarkAsync(SftpRemoteSession session, SftpBookmark bm) { session.Bookmarks.Remove(bm); await SaveBookmarksAsync(session); ShowStatus("已移除", $"路径 {bm.DisplayLabel} 已从书签中移除", InfoBarSeverity.Success); }
        private async Task LoadBookmarksAsync(SftpRemoteSession session) { try { var ps = await _storage.LoadConnectionsAsync(); var p = ps.FirstOrDefault(p => p.Id == session.Profile.Id); if (p != null) session.Bookmarks = p.SftpBookmarks ?? new List<SftpBookmark>(); } catch { } }
        private async Task SaveBookmarksAsync(SftpRemoteSession session) { try { var ps = await _storage.LoadConnectionsAsync(); var p = ps.FirstOrDefault(p => p.Id == session.Profile.Id); if (p != null) { p.SftpBookmarks = session.Bookmarks; await _storage.SaveConnectionsAsync(ps); } } catch (Exception ex) { ShowStatus("书签保存失败", ex.Message, InfoBarSeverity.Error); } }

        // ── Host Key Verification ─────────────────────────────────────────────

        private async Task<bool> ShowNewHostKeyDialogAsync(string host, int port, string algorithm, string fingerprint)
        {
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () => { try { var dlg = BuildHostKeyDialog("未知主机", $"首次连接到 {host}:{port}，请确认主机指纹是否正确。", algorithm, fingerprint, "信任并连接"); var r = await dlg.ShowAsync(); var ok = r == ContentDialogResult.Primary; if (ok) _knownHosts.Trust(host, port, algorithm, fingerprint); tcs.SetResult(ok); } catch (Exception ex) { tcs.SetException(ex); } });
            return await tcs.Task;
        }

        private async Task<bool> ShowChangedHostKeyDialogAsync(string host, int port, string algorithm, string fingerprint)
        {
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () => { try { var dlg = BuildHostKeyDialog("主机指纹已变更", $"{host}:{port} 的主机指纹和本地记录不一致，请确认不是中间人攻击。", algorithm, fingerprint, "更新并连接"); var r = await dlg.ShowAsync(); var ok = r == ContentDialogResult.Primary; if (ok) _knownHosts.Trust(host, port, algorithm, fingerprint); tcs.SetResult(ok); } catch (Exception ex) { tcs.SetException(ex); } });
            return await tcs.Task;
        }

        private ContentDialog BuildHostKeyDialog(string title, string body, string algorithm, string fingerprint, string primaryText)
        {
            return new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = title, Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = $"算法: {algorithm}", FontFamily = new FontFamily("Consolas"), FontSize = 12 }, new TextBlock { Text = fingerprint, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 } } }, PrimaryButtonText = primaryText, CloseButtonText = "拒绝", DefaultButton = ContentDialogButton.Primary };
        }

        // ── Path helpers ──────────────────────────────────────────────────────

        private static string CombineRemote(string left, string right) { if (string.IsNullOrEmpty(left) || left == ".") return right; if (left == "/") return "/" + right.Trim('/'); return left.TrimEnd('/') + "/" + right.Trim('/'); }
        private static string GetRemoteParent(string path) { if (string.IsNullOrWhiteSpace(path) || path == "/" || path == ".") return "/"; var t = path.TrimEnd('/'); var i = t.LastIndexOf('/'); if (i <= 0) return "/"; return t[..i]; }
        private static string GetRemoteName(string path) { var t = path.TrimEnd('/'); var i = t.LastIndexOf('/'); return i >= 0 ? t[(i + 1)..] : t; }
    }
}
