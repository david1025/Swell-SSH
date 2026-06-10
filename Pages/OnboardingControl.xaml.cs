using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using SwellSSH.Models;
using SwellSSH.Services;

namespace SwellSSH.Pages
{
    public sealed partial class OnboardingControl : UserControl
    {
        private int _currentPage = 0;
        private Brush? _activeBrush;
        private Brush? _inactiveBrush;
        
        private readonly ConnectionStorage _storage = new();
        private TerminalSettings _settings = new();
        private bool _isInitializing = true;
        
        public OnboardingControl()
        {
            this.InitializeComponent();
            this.Loaded += OnboardingControl_Loaded;
        }
        
        private async void OnboardingControl_Loaded(object sender, RoutedEventArgs e)
        {
            // 启动背景环境球体动画
            AmbientLightStoryboard.Begin();
            
            // 加载当前设置
            _settings = await _storage.LoadSettingsAsync();
            
            // 同步UI初始状态
            switch (_settings.ColorScheme)
            {
                case "Default Light": ThemeSegmented.SelectedIndex = 0; break;
                case "One Dark": ThemeSegmented.SelectedIndex = 1; break;
                default: ThemeSegmented.SelectedIndex = 2; break;
            }
            
            switch (_settings.BackdropType)
            {
                case "Mica": BackdropSegmented.SelectedIndex = 0; break;
                case "Acrylic": BackdropSegmented.SelectedIndex = 1; break;
                default: BackdropSegmented.SelectedIndex = 2; break;
            }
            
            switch (_settings.CursorStyle)
            {
                case "Underline": CursorUnderline.IsChecked = true; break;
                case "Bar": CursorBar.IsChecked = true; break;
                default: CursorBlock.IsChecked = true; break;
            }
            
            _isInitializing = false;
        }
        
        public void ResetState()
        {
            _currentPage = 0;
            UpdatePageVisibility();
        }
        
        private void UpdatePageVisibility()
        {
            // 切换页面面板
            Page0.Visibility = _currentPage == 0 ? Visibility.Visible : Visibility.Collapsed;
            Page1.Visibility = _currentPage == 1 ? Visibility.Visible : Visibility.Collapsed;
            Page2.Visibility = _currentPage == 2 ? Visibility.Visible : Visibility.Collapsed;
            
            // 按钮显示状态
            PrevButton.Visibility = _currentPage > 0 ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Content = _currentPage == 2 ? "立即开启" : "下一步";
            
            // 进度圆点状态切换动画效果
            if (_activeBrush == null)
            {
                _activeBrush = Dot0.Fill;
                _inactiveBrush = Dot1.Fill;
            }
            
            var activeBrush = _activeBrush;
            var inactiveBrush = _inactiveBrush;
            
            Dot0.Width = _currentPage == 0 ? 24 : 8;
            Dot0.Fill = _currentPage == 0 ? activeBrush : inactiveBrush;
            
            Dot1.Width = _currentPage == 1 ? 24 : 8;
            Dot1.Fill = _currentPage == 1 ? activeBrush : inactiveBrush;
            
            Dot2.Width = _currentPage == 2 ? 24 : 8;
            Dot2.Fill = _currentPage == 2 ? activeBrush : inactiveBrush;
        }
        
        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 0)
            {
                _currentPage--;
                UpdatePageVisibility();
            }
        }
        
        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < 2)
            {
                _currentPage++;
                UpdatePageVisibility();
            }
            else
            {
                // 在最后一页，保存设置并完成
                await CompleteOnboardingAsync();
            }
        }
        
        private async Task CompleteOnboardingAsync()
        {
            NextButton.IsEnabled = false;
            PrevButton.IsEnabled = false;
            
            // 保存个性化设置
            if (ThemeSegmented.SelectedItem is CommunityToolkit.WinUI.Controls.SegmentedItem themeItem && themeItem.Tag is string themeStr)
            {
                if (themeStr == "Light") _settings.ColorScheme = "Default Light";
                else if (themeStr == "Dark") _settings.ColorScheme = "One Dark";
            }
            
            if (BackdropSegmented.SelectedItem is CommunityToolkit.WinUI.Controls.SegmentedItem backdropItem && backdropItem.Tag is string backdropStr)
                _settings.BackdropType = backdropStr;
            
            if (CursorUnderline.IsChecked == true)
                _settings.CursorStyle = "Underline";
            else if (CursorBar.IsChecked == true)
                _settings.CursorStyle = "Bar";
            else
                _settings.CursorStyle = "Block";
                
            await _storage.SaveSettingsAsync(_settings);
            
            // 立即生效外观
            if (MainWindow.Instance != null)
            {
                await MainWindow.Instance.ApplySavedSettingsAsync();
            }
            
            // 标记已完成引导
            _settings.OnboardingCompleted = true;
            await _storage.SaveSettingsAsync(_settings);
            
            // 关闭引导界面
            if (MainWindow.Instance != null)
            {
                MainWindow.Instance.HideOnboarding();
            }
            
            NextButton.IsEnabled = true;
            PrevButton.IsEnabled = true;
        }
        
        private async void ThemeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e) 
        { 
            if (_isInitializing) return;
            if (ThemeSegmented.SelectedItem is CommunityToolkit.WinUI.Controls.SegmentedItem themeItem && themeItem.Tag is string themeStr)
            {
                if (themeStr == "Light") _settings.ColorScheme = "Default Light";
                else if (themeStr == "Dark") _settings.ColorScheme = "One Dark";
                else _settings.ColorScheme = "System";
                
                await _storage.SaveSettingsAsync(_settings);
                if (MainWindow.Instance != null) await MainWindow.Instance.ApplySavedSettingsAsync();
            }
        }
        
        private async void BackdropSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e) 
        { 
            if (_isInitializing) return;
            if (BackdropSegmented.SelectedItem is CommunityToolkit.WinUI.Controls.SegmentedItem backdropItem && backdropItem.Tag is string backdropStr)
            {
                _settings.BackdropType = backdropStr;
                await _storage.SaveSettingsAsync(_settings);
                if (MainWindow.Instance != null) await MainWindow.Instance.ApplySavedSettingsAsync();
            }
        }
        
        private async void CursorStyle_Checked(object sender, RoutedEventArgs e) 
        { 
            if (_isInitializing) return;
            if (CursorUnderline?.IsChecked == true) _settings.CursorStyle = "Underline";
            else if (CursorBar?.IsChecked == true) _settings.CursorStyle = "Bar";
            else if (CursorBlock?.IsChecked == true) _settings.CursorStyle = "Block";
            else return;
            
            await _storage.SaveSettingsAsync(_settings);
        }
    }
}
