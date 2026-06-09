using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SwellSSH.Models;
using SwellSSH.Services;
using SwellSSH.Terminal;

namespace SwellSSH.Pages
{
    /// <summary>Simple view-model row for the connection list.</summary>
    public abstract class SidebarItemViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ConnectionGroupViewModel : SidebarItemViewModel
    {
        public string Name { get; }
        public ObservableCollection<ConnectionItemViewModel> Children { get; } = new();

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ChevronGlyph));
                }
            }
        }
        public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C"; // Down / Right arrow

        public ConnectionGroupViewModel(string name) => Name = name;
    }

    public class ConnectionItemViewModel : SidebarItemViewModel
    {
        public ConnectionProfile Profile { get; }
        public string Group => Profile.Group;
        public string Name => Profile.Name;

        private bool _isIpVisible;
        public bool IsIpVisible
        {
            get => _isIpVisible;
            set
            {
                _isIpVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayHostPort));
                OnPropertyChanged(nameof(EyeGlyph));
            }
        }

        public string DisplayHostPort =>
            IsIpVisible
                ? $"{Profile.Username}@{Profile.Host}:{Profile.Port}"
                : $"{Profile.Username}@***.***.***.***:{Profile.Port}";

        public string EyeGlyph => IsIpVisible ? "\uE7B3" : "\uE7A8";

        private string _statsText = "";
        private bool _monitoringVisible;

        public string StatsText
        {
            get => _statsText;
            set { _statsText = value; OnPropertyChanged(); }
        }

        public Visibility IsMonitoringVisible =>
            _monitoringVisible ? Visibility.Visible : Visibility.Collapsed;

        public void ApplyStats(ServerStats s)
        {
            if (s.HasError)
            {
                StatsText = $"🔴 {s.ErrorMessage?.Split('\n')[0] ?? "监控失败"}".Substring(0, Math.Min(28, (s.ErrorMessage?.Length ?? 0) + 3));
                _monitoringVisible = true;
            }
            else if (s.IsAvailable)
            {
                StatsText = $"CPU {s.CpuPercent,4:F0}%  RAM {s.RamPercent,4:F0}%  Disk {s.DiskPercent,3:F0}%";
                _monitoringVisible = true;
            }
            else
            {
                StatsText = s.RamPercent >= 0
                    ? $"RAM {s.RamPercent,4:F0}%  Disk {s.DiskPercent,3:F0}%  …"
                    : "正在获取监控数据…";
                _monitoringVisible = true;
            }
            OnPropertyChanged(nameof(StatsText));
            OnPropertyChanged(nameof(IsMonitoringVisible));
        }

        public ConnectionItemViewModel(ConnectionProfile profile) => Profile = profile;
    }

    public sealed partial class MainPage : Page
    {
        private readonly ConnectionStorage _storage = new();
        private readonly KnownHostsService _knownHosts = new();
        public ObservableCollection<SidebarItemViewModel> FlatSidebarItems { get; } = new();
        private readonly List<ConnectionGroupViewModel> _groups = new();
        // Map profileId → ViewModel for fast stats lookup
        private readonly Dictionary<string, ConnectionItemViewModel> _vmById = new();

        public MainPage()
        {
            this.InitializeComponent();
            _ = LoadConnectionsAsync();

            if (MainWindow.Instance != null)
                MainWindow.Instance.ThemeChanged += OnThemeChanged;
            TerminalSettings.GlobalSettingsChanged += OnGlobalSettingsChanged;

            // Subscribe to monitoring stats updates
            ServerMonitorService.Instance.StatsUpdated += OnStatsUpdated;

            this.Unloaded += (_, _) =>
            {
                if (MainWindow.Instance != null)
                    MainWindow.Instance.ThemeChanged -= OnThemeChanged;
                TerminalSettings.GlobalSettingsChanged -= OnGlobalSettingsChanged;
                ServerMonitorService.Instance.StatsUpdated -= OnStatsUpdated;
            };

            SetupKeyboardShortcuts();
        }

        private void OnGlobalSettingsChanged(TerminalSettings settings)
        {
            _cachedSettings = settings;
            foreach (TabViewItem tab in TerminalTabView.TabItems)
            {
                if (tab.Content is Grid grid && grid.Children.FirstOrDefault(c => c is TerminalView) is TerminalView terminalView)
                {
                    terminalView.ApplySettings(settings);
                }
            }
            SyncThemeMenuCheckedState(settings.ColorScheme);
        }

        private void SetupKeyboardShortcuts()
        {
            // Ctrl+T: New tab (invokes the add tab button logic)
            var ctrlT = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
            { 
                Modifiers = Windows.System.VirtualKeyModifiers.Control, 
                Key = Windows.System.VirtualKey.T 
            };
            ctrlT.Invoked += (s, e) => 
            { 
                e.Handled = true; 
                TerminalTabView_AddTabButtonClick(TerminalTabView, null!);
            };
            this.KeyboardAccelerators.Add(ctrlT);

            // Ctrl+W: Close current tab
            var ctrlW = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
            { 
                Modifiers = Windows.System.VirtualKeyModifiers.Control, 
                Key = Windows.System.VirtualKey.W 
            };
            ctrlW.Invoked += (s, e) => 
            { 
                e.Handled = true; 
                if (TerminalTabView.SelectedItem is TabViewItem tab) 
                    CloseTab(tab); 
            };
            this.KeyboardAccelerators.Add(ctrlW);

            // Ctrl+Tab: Next tab
            var ctrlTab = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
            { 
                Modifiers = Windows.System.VirtualKeyModifiers.Control, 
                Key = Windows.System.VirtualKey.Tab 
            };
            ctrlTab.Invoked += (s, e) => 
            {
                e.Handled = true;
                if (TerminalTabView.TabItems.Count > 1) 
                    TerminalTabView.SelectedIndex = (TerminalTabView.SelectedIndex + 1) % TerminalTabView.TabItems.Count;
            };
            this.KeyboardAccelerators.Add(ctrlTab);

            // Ctrl+Shift+Tab: Previous tab
            var ctrlShiftTab = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
            { 
                Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, 
                Key = Windows.System.VirtualKey.Tab 
            };
            ctrlShiftTab.Invoked += (s, e) => 
            {
                e.Handled = true;
                if (TerminalTabView.TabItems.Count > 1) 
                    TerminalTabView.SelectedIndex = (TerminalTabView.SelectedIndex - 1 + TerminalTabView.TabItems.Count) % TerminalTabView.TabItems.Count;
            };
            this.KeyboardAccelerators.Add(ctrlShiftTab);

            // Ctrl+1~9: Jump to specific tab
            for (int i = 1; i <= 9; i++)
            {
                var key = (Windows.System.VirtualKey)(Windows.System.VirtualKey.Number0 + i);
                var numAcc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
                { 
                    Modifiers = Windows.System.VirtualKeyModifiers.Control, 
                    Key = key 
                };
                int targetIndex = i - 1;
                numAcc.Invoked += (s, e) => 
                {
                    e.Handled = true;
                    if (targetIndex < TerminalTabView.TabItems.Count)
                        TerminalTabView.SelectedIndex = targetIndex;
                };
                this.KeyboardAccelerators.Add(numAcc);
            }
        }

        private async void OnThemeChanged(ElementTheme newTheme)
        {
            var settings = await _storage.LoadSettingsAsync();
            // Explicitly set the color scheme because the file might not be saved yet
            settings.ColorScheme = newTheme == ElementTheme.Light ? "Default Light" : "One Dark";
            
            foreach (TabViewItem tab in TerminalTabView.TabItems)
            {
                if (tab.Content is Grid grid && grid.Children.FirstOrDefault(c => c is TerminalView) is TerminalView terminalView)
                {
                    terminalView.ApplySettings(settings);
                }
            }

            SyncThemeMenuCheckedState(settings.ColorScheme);
        }

        private async Task LoadConnectionsAsync()
        {
            var profiles = await _storage.LoadConnectionsAsync();
            _groups.Clear();
            _vmById.Clear();

            // 1. Group profiles
            var grouped = profiles.GroupBy(p => string.IsNullOrEmpty(p.Group) ? "默认分组" : p.Group);
            foreach (var g in grouped)
            {
                var groupVm = new ConnectionGroupViewModel(g.Key);
                foreach (var p in g)
                {
                    var itemVm = new ConnectionItemViewModel(p);
                    groupVm.Children.Add(itemVm);
                    _vmById[p.Id] = itemVm;
                }
                _groups.Add(groupVm);
            }

            // 2. Build flat list
            RefreshFlatSidebarList();

            // Sync settings theme menu
            var settings = await _storage.LoadSettingsAsync();
            SyncThemeMenuCheckedState(settings.ColorScheme);

            // Start/stop background monitoring per profile
            ServerMonitorService.Instance.Sync(profiles);
        }

        private void RefreshFlatSidebarList()
        {
            FlatSidebarItems.Clear();
            foreach (var g in _groups)
            {
                FlatSidebarItems.Add(g);
                if (g.IsExpanded)
                {
                    foreach (var child in g.Children)
                        FlatSidebarItems.Add(child);
                }
            }
            UpdateEmptyState();
        }

        private void GroupHeader_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionGroupViewModel g)
            {
                e.Handled = true;
                g.IsExpanded = !g.IsExpanded;

                // Smart update flat list (insert/remove instead of full rebuild for animation-friendly UI)
                int groupIndex = FlatSidebarItems.IndexOf(g);
                if (groupIndex < 0) return;

                if (g.IsExpanded)
                {
                    int insertAt = groupIndex + 1;
                    foreach (var child in g.Children)
                        FlatSidebarItems.Insert(insertAt++, child);
                }
                else
                {
                    for (int i = 0; i < g.Children.Count; i++)
                    {
                        if (groupIndex + 1 < FlatSidebarItems.Count && FlatSidebarItems[groupIndex + 1] is ConnectionItemViewModel)
                            FlatSidebarItems.RemoveAt(groupIndex + 1);
                    }
                }
            }
        }

        /// <summary>Called from ServerMonitorService on thread-pool; marshal to UI thread.</summary>
        private void OnStatsUpdated(ServerStats stats)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_vmById.TryGetValue(stats.ConnectionId, out var vm))
                    vm.ApplyStats(stats);
            });
        }

        /// <summary>将 TabStripFooter 主题菜单的选中项与当前配色方案对齐。</summary>
        private void SyncThemeMenuCheckedState(string colorScheme)
        {
            // Map of theme name -> RadioMenuFlyoutItem
            var map = new System.Collections.Generic.Dictionary<string, RadioMenuFlyoutItem>
            {
                ["One Dark"]        = ThemeOneDark,
                ["Dracula"]         = ThemeDracula,
                ["Solarized Dark"]  = ThemeSolarized,
                ["Catppuccin Mocha"]= ThemeCatppuccin,
                ["Tokyo Night"]     = ThemeTokyoNight,
                ["Nord"]            = ThemeNord,
                ["Gruvbox Dark"]    = ThemeGruvbox,
                ["Default Light"]   = ThemeDefaultLight,
            };

            foreach (var kv in map)
                kv.Value.IsChecked = kv.Key == colorScheme;
        }

        private void UpdateEmptyState()
        {
            if (EmptyStatePanel != null)
                EmptyStatePanel.Visibility = TerminalTabView.TabItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Connection list actions ───────────────────────────────────────────

        private async void AddConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = new ConnectionProfile();
            bool saved = await ShowConnectionDialogAsync(profile, isNew: true);
            if (!saved) return;

            var profiles = await _storage.LoadConnectionsAsync();
            profiles.Add(profile);
            await _storage.SaveConnectionsAsync(profiles);
            await LoadConnectionsAsync();
        }

        private async void EditConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionListView.SelectedItem is ConnectionItemViewModel vm)
                await EditProfileAsync(vm);
        }

        private async void DeleteConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionListView.SelectedItem is ConnectionItemViewModel vm)
                await DeleteProfileAsync(vm);
        }

        // ── Context Menu handlers ─────────────────────────────────────────────

        private void ConnectMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
                OpenTerminalTab(vm.Profile);
        }

        private void ToggleIpVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
                vm.IsIpVisible = !vm.IsIpVisible;
        }

        // ── Quick Connect ────────────────────────────────────────────────────────────────────

        private void QuickConnectBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                DoQuickConnect();
            }
        }

        private void QuickConnectButton_Click(object sender, RoutedEventArgs e) => DoQuickConnect();

        private void DoQuickConnect()
        {
            var input = QuickConnectBox.Text.Trim();
            if (string.IsNullOrEmpty(input)) return;

            if (!TryParseQuickConnect(input, out var profile))
            {
                // Briefly highlight the box to signal bad input
                QuickConnectBox.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
                _ = Task.Delay(1500).ContinueWith(_ =>
                    DispatcherQueue.TryEnqueue(() => QuickConnectBox.ClearValue(TextBox.BorderBrushProperty)));
                return;
            }

            QuickConnectBox.Text = "";
            OpenTerminalTab(profile);
        }

        /// <summary>解析 [user@]host[:port] 格式到临时 ConnectionProfile。</summary>
        private static bool TryParseQuickConnect(string input, out ConnectionProfile profile)
        {
            profile = new ConnectionProfile { Name = input };

            string user = "root";
            string host = input;
            int port = 22;

            // user@...
            if (host.Contains('@'))
            {
                int at = host.LastIndexOf('@');
                user = host[..at];
                host = host[(at + 1)..];
            }

            // [...]:port  (IPv6)
            if (host.StartsWith('['))
            {
                int close = host.IndexOf(']');
                if (close > 0)
                {
                    string ipv6 = host[1..close];
                    string rest = host[(close + 1)..];
                    if (rest.StartsWith(':') && int.TryParse(rest[1..], out int p6))
                        port = p6;
                    host = ipv6;
                }
            }
            else if (host.Count(c => c == ':') == 1)
            {
                var parts = host.Split(':');
                if (int.TryParse(parts[1], out int p)) { port = p; host = parts[0]; }
            }

            if (!IsValidHost(host)) return false;

            profile.Username = string.IsNullOrEmpty(user) ? "root" : user;
            profile.Host     = host;
            profile.Port     = port;
            profile.Name     = $"{user}@{host}:{port}";
            return true;
        }

        private async void EditMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
                await EditProfileAsync(vm);
        }

        private async void DeleteMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
                await DeleteProfileAsync(vm);
        }

        // ── Helper methods for Edit/Delete ──────────────────────────────────

        private async Task EditProfileAsync(ConnectionItemViewModel vm)
        {
            bool saved = await ShowConnectionDialogAsync(vm.Profile, isNew: false);
            if (!saved) return;

            var profiles = await _storage.LoadConnectionsAsync();
            var idx = profiles.FindIndex(p => p.Id == vm.Profile.Id);
            if (idx >= 0) profiles[idx] = vm.Profile;
            await _storage.SaveConnectionsAsync(profiles);
            await LoadConnectionsAsync();
        }

        private async Task DeleteProfileAsync(ConnectionItemViewModel vm)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "删除连接",
                Content = $"确定要删除「{vm.Name}」吗？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var profiles = await _storage.LoadConnectionsAsync();
            profiles.RemoveAll(p => p.Id == vm.Profile.Id);
            await _storage.SaveConnectionsAsync(profiles);
            await LoadConnectionsAsync();
        }

        // ── Connection list actions ───────────────────────────────────────────

        private void ConnectionListView_DoubleTapped(object sender,
            Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (ConnectionListView.SelectedItem is ConnectionItemViewModel vm)
                OpenTerminalTab(vm.Profile);
        }

        private void TerminalTabView_AddTabButtonClick(TabView sender, object args) { }

        // ── Theme picker (TabStripFooter) ──────────────────────────────────────

        private TerminalSettings? _cachedSettings;

        private async void ThemeMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item) return;
            string scheme = item.Tag?.ToString() ?? "One Dark";

            if (_cachedSettings == null)
                _cachedSettings = await _storage.LoadSettingsAsync();

            _cachedSettings.ColorScheme = scheme;
            await _storage.SaveSettingsAsync(_cachedSettings);
            
            TerminalSettings.NotifyGlobalSettingsChanged(_cachedSettings);
        }

        private void TerminalTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            CloseTab(args.Tab);
        }

        private void CloseTab(TabViewItem tab)
        {
            if (tab.Tag is TerminalSession session)
                session.Dispose();

            TerminalTabView.TabItems.Remove(tab);
            UpdateEmptyState();
        }

        private async void OpenTerminalTab(ConnectionProfile profile)
        {
            // ── Phase 4 terminal view: Win2D Canvas rendering + Keyboard Input ──
            var terminalView = new TerminalView();
            var settings = await _storage.LoadSettingsAsync();
            terminalView.ApplySettings(settings);

            // Status bar at bottom
            var statusBar = new InfoBar
            {
                IsOpen = true,
                Severity = InfoBarSeverity.Informational,
                Title = "正在连接...",
                Message = $"{profile.Username}@{profile.Host}:{profile.Port}"
            };

            var tabContent = new Grid();
            tabContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            tabContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(terminalView, 0);
            Grid.SetRow(statusBar, 1);
            tabContent.Children.Add(terminalView);
            tabContent.Children.Add(statusBar);

            var tab = new TabViewItem
            {
                Header = profile.Name,
                IconSource = new FontIconSource
                {
                    Glyph = "\uE895", // Sync
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange)
                },
                Content = tabContent
            };

            // ── Tab Context Menu ────────────────────────────────────────────────
            var flyout = new MenuFlyout();

            var closeItem = new MenuFlyoutItem { Text = "关闭标签" };
            closeItem.Click += (_, _) => CloseTab(tab);

            var closeOthersItem = new MenuFlyoutItem { Text = "关闭其他标签" };
            closeOthersItem.Click += (_, _) =>
            {
                var tabsToRemove = TerminalTabView.TabItems.Cast<TabViewItem>().Where(t => t != tab).ToList();
                foreach (var t in tabsToRemove) CloseTab(t);
            };

            var closeRightItem = new MenuFlyoutItem { Text = "关闭右侧标签" };
            closeRightItem.Click += (_, _) =>
            {
                int index = TerminalTabView.TabItems.IndexOf(tab);
                var tabsToRemove = TerminalTabView.TabItems.Cast<TabViewItem>().Skip(index + 1).ToList();
                foreach (var t in tabsToRemove) CloseTab(t);
            };

            flyout.Items.Add(closeItem);
            flyout.Items.Add(closeOthersItem);
            flyout.Items.Add(closeRightItem);
            tab.ContextFlyout = flyout;

            // Create and wire up the session
            var session = new TerminalSession(profile);
            tab.Tag = session;  // so TabCloseRequested can dispose it

            // ── Host Key Verification ───────────────────────────────────────────
            session.Transport.HostKeyVerifier = async (host, port, algorithm, fingerprint) =>
            {
                var trusted = _knownHosts.Check(host, port, algorithm, fingerprint);
                if (trusted == true)  return true;   // already known + unchanged
                if (trusted == false) return await ShowChangedHostKeyDialogAsync(host, port, algorithm, fingerprint);
                return await ShowNewHostKeyDialogAsync(host, port, algorithm, fingerprint);
            };

            // Handle state changes
            session.StateChanged += (_, state) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    switch (state)
                    {
                        case TerminalSession.SessionState.Connected:
                            tab.Header = profile.Name;
                            tab.IconSource = new FontIconSource
                            {
                                Glyph = "\uE8C8", // Terminal
                                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
                            };
                            statusBar.Severity = InfoBarSeverity.Success;
                            statusBar.Title = "已连接";
                            statusBar.Message = $"{profile.Username}@{profile.Host}:{profile.Port}";

                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(10000);
                                DispatcherQueue.TryEnqueue(() => statusBar.IsOpen = false);
                            });
                            break;

                        case TerminalSession.SessionState.Error:
                            tab.Header = profile.Name;
                            tab.IconSource = new FontIconSource
                            {
                                Glyph = "\uEA39", // Error
                                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red)
                            };
                            statusBar.Severity = InfoBarSeverity.Error;
                            statusBar.Title = "连接失败";
                            statusBar.Message = session.LastError ?? "未知错误";
                            break;
                    }
                });
            };

            // Handle title updates from OSC sequences
            session.TitleChanged += title =>
            {
                DispatcherQueue.TryEnqueue(() => tab.Header = title);
            };

            // Attach session to the Win2D View
            terminalView.AttachSession(session);

            TerminalTabView.TabItems.Add(tab);
            TerminalTabView.SelectedItem = tab;
            UpdateEmptyState();
        }

        // ── Host Key dialogs ─────────────────────────────────────────────────────────────────

        private async Task<bool> ShowNewHostKeyDialogAsync(
            string host, int port, string algorithm, string fingerprint)
        {
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = this.XamlRoot,
                        Title = "🔑 未知主机",
                        Content = new StackPanel { Spacing = 8, Children =
                        {
                            new TextBlock { Text = $"首次连接到 {host}:{port}，请确认主机指纹是否正确。", TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = $"算法：{algorithm}", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 },
                            new TextBlock { Text = fingerprint, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }
                        }},
                        PrimaryButtonText = "信任并连接",
                        CloseButtonText   = "拒绝",
                        DefaultButton     = ContentDialogButton.Primary
                    };
                    var result = await dialog.ShowAsync();
                    bool ok = result == ContentDialogResult.Primary;
                    if (ok) _knownHosts.Trust(host, port, algorithm, fingerprint);
                    tcs.SetResult(ok);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return await tcs.Task;
        }

        private async Task<bool> ShowChangedHostKeyDialogAsync(
            string host, int port, string algorithm, string fingerprint)
        {
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = this.XamlRoot,
                        Title = "⚠️ 主机指纹已变更！",
                        Content = new StackPanel { Spacing = 8, Children =
                        {
                            new TextBlock
                            {
                                Text = $"{host}:{port} 的主机密钒与之前保存的不一致！\n" +
                                       "这可能意味着副本攻击（MITM）或服务器密钒已更新。\n" +
                                       "确认新指纹后才可信任。",
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
                            },
                            new TextBlock { Text = $"新算法：{algorithm}", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 },
                            new TextBlock { Text = fingerprint, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }
                        }},
                        PrimaryButtonText = "更新并信任",
                        CloseButtonText   = "拒绝",
                        DefaultButton     = ContentDialogButton.Close
                    };
                    var result = await dialog.ShowAsync();
                    bool ok = result == ContentDialogResult.Primary;
                    if (ok) _knownHosts.Trust(host, port, algorithm, fingerprint);
                    tcs.SetResult(ok);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return await tcs.Task;
        }

        // ── Connection edit dialog ────────────────────────────────────────────

        private async Task<bool> ShowConnectionDialogAsync(ConnectionProfile profile, bool isNew)
        {
            var nameBox = new TextBox { Header = "连接名称", Text = profile.Name, PlaceholderText = "My Server" };

            var existingGroups = _groups.Select(g => g.Name).Distinct().Where(g => !string.IsNullOrEmpty(g)).ToList();
            if (!existingGroups.Contains("默认分组")) existingGroups.Insert(0, "默认分组");
            var groupCombo = new ComboBox
            {
                Header = "分组",
                IsEditable = true,
                ItemsSource = existingGroups,
                Text = string.IsNullOrEmpty(profile.Group) ? "默认分组" : profile.Group,
                PlaceholderText = "选择或输入新分组",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var hostBox = new TextBox { Header = "主机地址", Text = profile.Host, PlaceholderText = "192.168.1.1" };
            // 主机输入时清除错误状态
            hostBox.TextChanged += (s, e) => SetFieldError(hostBox, false);

            var portBox = new NumberBox { Header = "端口", Value = profile.Port, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var userBox = new TextBox { Header = "用户名", Text = profile.Username, PlaceholderText = "root" };
            userBox.TextChanged += (s, e) => SetFieldError(userBox, false);

            var authCombo = new ComboBox { Header = "认证方式", ItemsSource = new[] { "Password", "PrivateKey" }, SelectedItem = profile.AuthType };

            string existingPwd = "";
            if (!string.IsNullOrEmpty(profile.EncryptedPassword))
                try { existingPwd = ConnectionStorage.DecryptSecret(profile.EncryptedPassword); } catch { }

            var pwdBox = new PasswordBox { Header = "密码", Password = existingPwd, PlaceholderText = "输入密码", Visibility = profile.AuthType == "Password" ? Visibility.Visible : Visibility.Collapsed };
            string currentPwd = existingPwd;
            pwdBox.PasswordChanged += (s, e) => currentPwd = pwdBox.Password;

            var keyPathBox = new TextBox { Header = "私钥路径", Text = profile.PrivateKeyPath, PlaceholderText = @"C:\Users\...\id_rsa", Visibility = profile.AuthType == "PrivateKey" ? Visibility.Visible : Visibility.Collapsed };

            authCombo.SelectionChanged += (s, e) =>
            {
                bool isPwd = authCombo.SelectedItem?.ToString() == "Password";
                pwdBox.Visibility = isPwd ? Visibility.Visible : Visibility.Collapsed;
                keyPathBox.Visibility = isPwd ? Visibility.Collapsed : Visibility.Visible;
            };

            // ── Keepalive ──────────────────────────────────────────────────────
            var keepAliveBox = new NumberBox
            {
                Header = "Keepalive 间隔（秒，0=关闭）",
                Value = profile.KeepAliveIntervalSeconds,
                Minimum = 0,
                Maximum = 300,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
            };

            // ── 监控开关 ──────────────────────────────────────────────────────
            var monitorSwitch = new ToggleSwitch
            {
                Header = "在连接列表开启性能监控",
                IsOn = profile.EnableMonitoring
            };
            var monitorInterval = new NumberBox
            {
                Header = "监控间隔（秒）",
                Value = profile.MonitorIntervalSeconds,
                Minimum = 3,
                Maximum = 60,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Visibility = profile.EnableMonitoring ? Visibility.Visible : Visibility.Collapsed
            };
            monitorSwitch.Toggled += (_, _) =>
                monitorInterval.Visibility = monitorSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;

            // ── 内联错误提示 ───────────────────────────────────────────────────
            var errorLabel = new TextBlock
            {
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            var content = new StackPanel { Spacing = 12, Width = 360 };
            content.Children.Add(nameBox);
            content.Children.Add(groupCombo);
            content.Children.Add(hostBox);
            content.Children.Add(portBox);
            content.Children.Add(userBox);
            content.Children.Add(authCombo);
            content.Children.Add(pwdBox);
            content.Children.Add(keyPathBox);
            content.Children.Add(keepAliveBox);
            content.Children.Add(monitorSwitch);
            content.Children.Add(monitorInterval);
            content.Children.Add(errorLabel);

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = isNew ? "新建连接" : "编辑连接",
                Content = new ScrollViewer { Content = content, MaxHeight = 540 },
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            // 保存前校验
            dialog.PrimaryButtonClick += (s, args) =>
            {
                string host = hostBox.Text.Trim();
                string user = userBox.Text.Trim();

                if (string.IsNullOrEmpty(host))
                {
                    args.Cancel = true;
                    SetFieldError(hostBox, true);
                    ShowDialogError(errorLabel, "主机地址不能为空");
                    return;
                }
                if (!IsValidHost(host))
                {
                    args.Cancel = true;
                    SetFieldError(hostBox, true);
                    ShowDialogError(errorLabel, "主机地址格式不正确，请输入合法的 IPv4、IPv6 或域名\n示例：192.168.1.1 / [::1] / my-server.example.com");
                    return;
                }
                if (string.IsNullOrEmpty(user))
                {
                    args.Cancel = true;
                    SetFieldError(userBox, true);
                    ShowDialogError(errorLabel, "用户名不能为空");
                    return;
                }
                errorLabel.Visibility = Visibility.Collapsed;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

            profile.Name     = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "New Connection";
            profile.Group    = groupCombo.Text.Trim().Length > 0 ? groupCombo.Text.Trim() : "默认分组";
            profile.Host     = hostBox.Text.Trim();
            profile.Port     = (int)portBox.Value;
            profile.Username = userBox.Text.Trim();
            profile.AuthType = authCombo.SelectedItem?.ToString() ?? "Password";
            profile.KeepAliveIntervalSeconds = double.IsNaN(keepAliveBox.Value) ? 0 : (int)keepAliveBox.Value;
            profile.EnableMonitoring = monitorSwitch.IsOn;
            profile.MonitorIntervalSeconds = double.IsNaN(monitorInterval.Value) ? 10 : Math.Max(3, (int)monitorInterval.Value);

            if (profile.AuthType == "Password" && !string.IsNullOrEmpty(currentPwd))
                profile.EncryptedPassword = ConnectionStorage.EncryptSecret(currentPwd);

            if (profile.AuthType == "PrivateKey")
                profile.PrivateKeyPath = keyPathBox.Text.Trim();

            return true;
        }

        // ── 字段错误高亮 ──────────────────────────────────────────────────────

        private static void SetFieldError(Control ctrl, bool hasError)
        {
            if (hasError)
                ctrl.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
            else
                ctrl.ClearValue(Control.BorderBrushProperty);
        }

        private static void ShowDialogError(TextBlock label, string message)
        {
            label.Text = message;
            label.Visibility = Visibility.Visible;
        }

        // ── 主机地址校验 ──────────────────────────────────────────────────────

        private static bool IsValidHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            host = host.Trim();

            // host:port 格式（非 IPv6）
            if (host.Contains(':') && !host.Contains(']'))
            {
                if (host.Count(c => c == ':') == 1)
                {
                    var parts = host.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int p) && p >= 0 && p <= 65535)
                        host = parts[0];
                }
            }

            // 去掉 IPv6 括号 [::1] → ::1
            if (host.StartsWith('[') && host.EndsWith(']'))
                host = host[1..^1];

            return IsValidIpv4(host) || IsValidIpv6(host) || IsValidDomain(host);
        }

        private static bool IsValidIpv4(string addr)
        {
            if (addr.Count(c => c == '.') != 3) return false;
            var parts = addr.Split('.');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) return false;
                foreach (char c in part)
                    if (c < '0' || c > '9') return false;
                if (part.Length > 1 && part[0] == '0') return false;
                if (!int.TryParse(part, out int num) || num < 0 || num > 255) return false;
            }
            return true;
        }

        private static bool IsValidIpv6(string addr)
        {
            if (IsValidIpv4(addr)) return false;
            return IPAddress.TryParse(addr, out var ip)
                && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        private static bool IsValidDomain(string addr)
        {
            if (addr.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (addr.Length > 253) return false;
            if (addr.StartsWith('.') || addr.EndsWith('.') || addr.Contains("..")) return false;
            var labels = addr.Split('.');
            if (labels.Length < 2) return false;
            var labelPattern = new Regex(@"^[a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?$");
            foreach (var label in labels)
            {
                if (string.IsNullOrEmpty(label) || label.Length > 63) return false;
                if (label.Length == 1) { if (!char.IsLetterOrDigit(label[0])) return false; }
                else if (!labelPattern.IsMatch(label)) return false;
                if (label.StartsWith('-') || label.EndsWith('-')) return false;
            }
            var tld = labels[^1];
            return tld.Length >= 2 && tld.Any(char.IsLetter);
        }
    }
}
