using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SwellSSH.Models;
using SwellSSH.Services;

namespace SwellSSH.Pages
{
    public class ColorSchemeItem
    {
        public string Name { get; set; } = "";
        public Microsoft.UI.Xaml.Media.SolidColorBrush ColorBrush { get; set; } = null!;
    }

    public sealed partial class SettingsPage : Page
    {
        private readonly ConnectionStorage _storage = new();
        private TerminalSettings _settings = new();
        private bool _isLoading;

        public ColorSchemeItem[] ColorSchemes { get; } =
        {
            new() { Name = "One Dark", ColorBrush = new(Windows.UI.Color.FromArgb(255, 40, 44, 52)) },
            new() { Name = "Dracula", ColorBrush = new(Windows.UI.Color.FromArgb(255, 40, 42, 54)) },
            new() { Name = "Solarized Dark", ColorBrush = new(Windows.UI.Color.FromArgb(255, 0, 43, 54)) },
            new() { Name = "Catppuccin Mocha", ColorBrush = new(Windows.UI.Color.FromArgb(255, 30, 30, 46)) },
            new() { Name = "Tokyo Night", ColorBrush = new(Windows.UI.Color.FromArgb(255, 26, 27, 38)) },
            new() { Name = "Nord", ColorBrush = new(Windows.UI.Color.FromArgb(255, 46, 52, 64)) },
            new() { Name = "Gruvbox Dark", ColorBrush = new(Windows.UI.Color.FromArgb(255, 40, 40, 40)) },
            new() { Name = "Default Light", ColorBrush = new(Windows.UI.Color.FromArgb(255, 250, 250, 250)) }
        };
        public string[] BackdropTypes { get; } = { "Mica", "Acrylic", "None" };
        public string[] FontFamilies { get; } = Microsoft.Graphics.Canvas.Text.CanvasTextFormat.GetSystemFontFamilies();

        public SettingsPage()
        {
            this.InitializeComponent();
            _ = LoadSettingsAsync();

            // 显示当前版本号
            var ver = AppUpdateService.CurrentVersion;
            AppVersionText.Text = ver.Major == 0 && ver.Minor == 0
                ? "版本 开发版 (未打包)"
                : $"版本 {ver.Major}.{ver.Minor}.{ver.Build}";
        }

        private async Task LoadSettingsAsync()
        {
            _isLoading = true;
            _settings = await _storage.LoadSettingsAsync();
            ApplyToUi(_settings);
            _isLoading = false;
        }

        private void ApplyToUi(TerminalSettings s)
        {
            FontFamilyCombo.ItemsSource = FontFamilies;
            foreach (var font in FontFamilies)
            {
                if (font == s.FontFamily)
                {
                    FontFamilyCombo.SelectedItem = font;
                    break;
                }
            }
            if (FontFamilyCombo.SelectedItem == null)
            {
                FontFamilyCombo.SelectedItem = "Consolas";
            }
            
            FontSizeSlider.Value = s.FontSize;
            foreach (var item in ColorSchemes)
            {
                if (item.Name == s.ColorScheme)
                {
                    ColorSchemeCombo.SelectedItem = item;
                    break;
                }
            }
            
            foreach (var backdrop in BackdropTypes)
            {
                if (backdrop == s.BackdropType)
                {
                    BackdropCombo.SelectedItem = backdrop;
                    break;
                }
            }
            if (BackdropCombo.SelectedItem == null)
            {
                BackdropCombo.SelectedItem = BackdropTypes[0]; // Fallback to Mica
            }
            CursorBlinkToggle.IsOn = s.CursorBlink;
            MinimizeOnCloseToggle.IsOn = s.MinimizeOnClose;

            switch (s.CursorStyle)
            {
                case "Underline": CursorUnderline.IsChecked = true; break;
                case "Bar": CursorBar.IsChecked = true; break;
                default: CursorBlock.IsChecked = true; break;
            }
        }

        private void FontSizeSlider_ValueChanged(object sender,
            Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _settings.FontSize = e.NewValue;
            SaveAndApplySettings();
        }

        private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FontFamilyCombo.SelectedItem is string font)
            {
                _settings.FontFamily = font;
                SaveAndApplySettings();
            }
        }

        private void ColorSchemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColorSchemeCombo.SelectedItem is ColorSchemeItem scheme)
            {
                _settings.ColorScheme = scheme.Name;
                SaveAndApplySettings();
            }
        }

        private void BackdropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BackdropCombo.SelectedItem is string backdrop)
            {
                _settings.BackdropType = backdrop;
                SaveAndApplySettings();
            }
        }

        private void CursorStyle_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                _settings.CursorStyle = rb.Tag?.ToString() ?? "Block";
                SaveAndApplySettings();
            }
        }

        private void CursorBlinkToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.CursorBlink = CursorBlinkToggle.IsOn;
            SaveAndApplySettings();
        }

        private void MinimizeOnCloseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.MinimizeOnClose = MinimizeOnCloseToggle.IsOn;
            SaveAndApplySettings();
        }

        private async void SaveAndApplySettings()
        {
            if (_isLoading) return;
            await _storage.SaveSettingsAsync(_settings);
            TerminalSettings.NotifyGlobalSettingsChanged(_settings);

            var theme = _settings.ColorScheme == "Default Light" ? ElementTheme.Light : ElementTheme.Dark;
            if (MainWindow.Instance != null)
            {
                MainWindow.Instance.SetTheme(theme);
                MainWindow.Instance.SystemBackdrop = _settings.BackdropType switch
                {
                    "Acrylic" => new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(),
                    "None"    => null,
                    _         => new Microsoft.UI.Xaml.Media.MicaBackdrop()
                };
            }
        }

        // ── 检查客户端更新 ──────────────────────────────────────────────────

        private async void CheckAppUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            CheckAppUpdateButton.IsEnabled = false;
            CheckAppUpdateButton.Content = "正在检查…";

            try
            {
                var updater = new AppUpdateService();
                var info = await updater.CheckAsync(CancellationToken.None);

                if (info == null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = "当前已是最新版本，或网络无法访问 GitHub。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                    return;
                }

                var confirmDialog = new ContentDialog
                {
                    Title = "发现新版本",
                    Content = $"是否将 SwellSSH 更新至 {info.TagName}？\n\n下载完成后应用将自动重启以完成更新。",
                    PrimaryButtonText = "确认更新",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                // 进度 UI
                var progressText = new TextBlock { Text = "准备下载…", Margin = new Thickness(0, 0, 0, 10) };
                var progressBar  = new ProgressBar { IsIndeterminate = true, Width = 320 };
                var stack        = new StackPanel { Children = { progressText, progressBar } };

                var progressDialog = new ContentDialog
                {
                    Title    = "正在更新 SwellSSH",
                    Content  = stack,
                    XamlRoot = this.XamlRoot
                };

                var progress = new Progress<ProgressDialogUpdate>(update =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        progressText.Text = update.StatusText;
                        if (update.PercentComplete.HasValue)
                        {
                            progressBar.IsIndeterminate = false;
                            progressBar.Value = update.PercentComplete.Value;
                        }
                    });
                });

                var updateTask = updater.DownloadVerifyAndExtractAsync(info, progress, CancellationToken.None);
                _ = progressDialog.ShowAsync();

                try
                {
                    var staging = await updateTask;
                    progressDialog.Hide();
                    await Task.Delay(50);

                    var readyDialog = new ContentDialog
                    {
                        Title             = "更新准备就绪",
                        Content           = $"新版本 {info.TagName} 下载并校验成功！\n\n点击\"立即重启\"后，应用将关闭并完成文件覆盖更新。",
                        PrimaryButtonText = "立即重启",
                        CloseButtonText   = "稍后",
                        DefaultButton     = ContentDialogButton.Primary,
                        XamlRoot          = this.XamlRoot
                    };

                    if (await readyDialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        updater.LaunchUpdater(staging);
                        Application.Current.Exit();
                    }
                }
                catch (Exception ex)
                {
                    progressDialog.Hide();
                    await Task.Delay(50);

                    var errDialog = new ContentDialog
                    {
                        Title           = "更新失败",
                        Content         = ex.Message,
                        CloseButtonText = "确定",
                        XamlRoot        = this.XamlRoot
                    };
                    await errDialog.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title           = "检查更新失败",
                    Content         = $"错误信息：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot        = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                CheckAppUpdateButton.IsEnabled = true;
                CheckAppUpdateButton.Content   = "检查更新";
            }
        }
    }
}
