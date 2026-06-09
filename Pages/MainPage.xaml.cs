using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SwellSSH.Models;
using SwellSSH.Services;
using SwellSSH.Terminal;

namespace SwellSSH.Pages
{
    /// <summary>Simple view-model row for the connection list.</summary>
    public class ConnectionItemViewModel
    {
        public ConnectionProfile Profile { get; }
        public string Name => Profile.Name;
        public string HostPort => $"{Profile.Username}@{Profile.Host}:{Profile.Port}";
        public string Group => Profile.Group;
        public ConnectionItemViewModel(ConnectionProfile profile) => Profile = profile;
    }

    public class GroupInfoList : System.Collections.Generic.List<object>
    {
        public object Key { get; set; } = "";
    }

    public sealed partial class MainPage : Page
    {
        private readonly ConnectionStorage _storage = new();
        public ObservableCollection<ConnectionItemViewModel> Connections { get; } = new();

        public MainPage()
        {
            this.InitializeComponent();
            _ = LoadConnectionsAsync();

            if (MainWindow.Instance != null)
            {
                MainWindow.Instance.ThemeChanged += OnThemeChanged;
            }
            TerminalSettings.GlobalSettingsChanged += OnGlobalSettingsChanged;

            this.Unloaded += (_, _) =>
            {
                if (MainWindow.Instance != null)
                    MainWindow.Instance.ThemeChanged -= OnThemeChanged;
                TerminalSettings.GlobalSettingsChanged -= OnGlobalSettingsChanged;
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
            Connections.Clear();
            foreach (var p in profiles)
                Connections.Add(new ConnectionItemViewModel(p));

            var groupedList = new ObservableCollection<GroupInfoList>();
            var realQuery = from item in Connections
                            group item by item.Group into g
                            select g;
            foreach (var g in realQuery)
            {
                var info = new GroupInfoList { Key = g.Key };
                info.AddRange(g);
                groupedList.Add(info);
            }

            ConnectionsCVS.Source = groupedList;
            UpdateEmptyState();

            // Sync theme menu checked state with persisted settings
            var settings = await _storage.LoadSettingsAsync();
            SyncThemeMenuCheckedState(settings.ColorScheme);
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
            EmptyStatePanel.Visibility = TerminalTabView.TabItems.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
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

            // ConnectAsync is intentionally NOT called here.
            // TerminalView.Canvas_CreateResources fires after the canvas is laid out
            // and measures the real pixel size, so it will call session.ConnectAsync()
            // with the correct cols/rows. This prevents the SSH welcome text from
            // wrapping at a wrong column width.
        }

        // ── Connection edit dialog ────────────────────────────────────────────

        private async Task<bool> ShowConnectionDialogAsync(ConnectionProfile profile, bool isNew)
        {
            var nameBox    = new TextBox { Header = "连接名称", Text = profile.Name, PlaceholderText = "My Server" };
            
            var existingGroups = Connections.Select(c => c.Group).Distinct().Where(g => !string.IsNullOrEmpty(g)).ToList();
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
            var hostBox    = new TextBox { Header = "主机地址", Text = profile.Host, PlaceholderText = "192.168.1.1" };
            var portBox    = new NumberBox { Header = "端口", Value = profile.Port, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var userBox    = new TextBox { Header = "用户名", Text = profile.Username, PlaceholderText = "root" };
            var authCombo  = new ComboBox { Header = "认证方式", ItemsSource = new[] { "Password", "PrivateKey" }, SelectedItem = profile.AuthType };
            
            string existingPwd = "";
            if (!string.IsNullOrEmpty(profile.EncryptedPassword))
            {
                try { existingPwd = ConnectionStorage.DecryptSecret(profile.EncryptedPassword); } catch { }
            }
            var pwdBox     = new PasswordBox { Header = "密码", Password = existingPwd, PlaceholderText = "输入密码", Visibility = profile.AuthType == "Password" ? Visibility.Visible : Visibility.Collapsed };
            
            string currentPwd = existingPwd;
            pwdBox.PasswordChanged += (s, e) => currentPwd = pwdBox.Password;

            var keyPathBox = new TextBox { Header = "私钥路径", Text = profile.PrivateKeyPath, PlaceholderText = @"C:\Users\...\id_rsa", Visibility = profile.AuthType == "PrivateKey" ? Visibility.Visible : Visibility.Collapsed };

            authCombo.SelectionChanged += (s, e) =>
            {
                bool isPwd = authCombo.SelectedItem?.ToString() == "Password";
                pwdBox.Visibility     = isPwd ? Visibility.Visible : Visibility.Collapsed;
                keyPathBox.Visibility = isPwd ? Visibility.Collapsed : Visibility.Visible;
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

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = isNew ? "新建连接" : "编辑连接",
                Content = new ScrollViewer { Content = content, MaxHeight = 500 },
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

            profile.Name     = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "New Connection";
            profile.Group    = groupCombo.Text.Trim().Length > 0 ? groupCombo.Text.Trim() : "默认分组";
            profile.Host     = hostBox.Text.Trim();
            profile.Port     = (int)portBox.Value;
            profile.Username = userBox.Text.Trim();
            profile.AuthType = authCombo.SelectedItem?.ToString() ?? "Password";

            if (profile.AuthType == "Password" && !string.IsNullOrEmpty(currentPwd))
                profile.EncryptedPassword = ConnectionStorage.EncryptSecret(currentPwd);

            if (profile.AuthType == "PrivateKey")
                profile.PrivateKeyPath = keyPathBox.Text.Trim();

            return true;
        }
    }
}
