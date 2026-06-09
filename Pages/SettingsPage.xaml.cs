using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SwellSSH.Models;
using SwellSSH.Services;

namespace SwellSSH.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private readonly ConnectionStorage _storage = new();
        private TerminalSettings _settings = new();

        public string[] ColorSchemes { get; } =
        {
            "One Dark",
            "Dracula",
            "Solarized Dark",
            "Catppuccin Mocha",
            "Tokyo Night",
            "Nord",
            "Gruvbox Dark",
            "Default Light"
        };
        public string[] BackdropTypes { get; } = { "Mica", "Acrylic", "None" };

        public SettingsPage()
        {
            this.InitializeComponent();
            _ = LoadSettingsAsync();
        }

        private async Task LoadSettingsAsync()
        {
            _settings = await _storage.LoadSettingsAsync();
            ApplyToUi(_settings);
        }

        private void ApplyToUi(TerminalSettings s)
        {
            FontSizeSlider.Value = s.FontSize;
            ColorSchemeCombo.SelectedItem = s.ColorScheme;
            BackdropCombo.SelectedItem = s.BackdropType;
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
        }

        private void ColorSchemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColorSchemeCombo.SelectedItem is string scheme)
                _settings.ColorScheme = scheme;
        }

        private void BackdropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BackdropCombo.SelectedItem is string backdrop)
                _settings.BackdropType = backdrop;
        }

        private void CursorStyle_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb)
                _settings.CursorStyle = rb.Tag?.ToString() ?? "Block";
        }

        private void CursorBlinkToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.CursorBlink = CursorBlinkToggle.IsOn;
        }

        private void MinimizeOnCloseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.MinimizeOnClose = MinimizeOnCloseToggle.IsOn;
        }

        private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            await _storage.SaveSettingsAsync(_settings);

            var infoBar = new InfoBar
            {
                Severity = InfoBarSeverity.Success,
                Title = "设置已保存",
                IsOpen = true
            };
            // Show a brief confirmation — auto-close after 2s
            (this.Content as StackPanel)?.Children.Insert(0, infoBar);
            await Task.Delay(2000);
            infoBar.IsOpen = false;
        }
    }
}
