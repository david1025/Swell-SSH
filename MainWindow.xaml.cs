using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUIEx;
using SwellSSH.Pages;
using SwellSSH.Services;

namespace SwellSSH
{
    public sealed partial class MainWindow : Window
    {
        public static MainWindow? Instance { get; private set; }

        private readonly WindowManager _windowManager;
        private bool _isHiddenToTray;
        private ElementTheme _currentTheme = ElementTheme.Default;

        private readonly ConnectionStorage _storage = new();

        public MainWindow()
        {
            Instance = this;
            this.InitializeComponent();

            // WinUIEx WindowManager — handles tray, size, persistence
            _windowManager = WindowManager.Get(this);
            _windowManager.Width    = 1400;
            _windowManager.Height   = 820;
            _windowManager.MinWidth  = 1100;
            _windowManager.MinHeight = 600;
            this.CenterOnScreen();

            // Custom title bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            this.Title = "SwellSSH";
            this.AppWindow.Title = "SwellSSH";
            SetIconSafe();

            // Apply saved theme
            _ = ApplySavedSettingsAsync();

            // Tray setup
            ConfigureTray();

            // Pane open/close → show/hide theme button and logo
            MainNav.PaneOpening += (_, _) => UpdateHeaderVisibility(isOpen: true);
            MainNav.PaneClosing  += (_, _) => UpdateHeaderVisibility(isOpen: false);
            UpdateHeaderVisibility(isOpen: false, animate: false);

            // Navigation
            MainNav.SelectionChanged += MainNav_SelectionChanged;
            MainNav.SelectedItem = ConnectionsNavItem;
        }

        // ── Settings persistence ─────────────────────────────────────────────

        private async Task ApplySavedSettingsAsync()
        {
            var settings = await _storage.LoadSettingsAsync();

            var theme = settings.ColorScheme == "Default Light"
                ? ElementTheme.Light
                : ElementTheme.Dark;

            SetTheme(theme);

            this.SystemBackdrop = settings.BackdropType switch
            {
                "Acrylic" => new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(),
                "None"    => null,
                _         => new Microsoft.UI.Xaml.Media.MicaBackdrop()
            };
        }

        // ── Navigation ───────────────────────────────────────────────────────

        private void MainNav_SelectionChanged(NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is not NavigationViewItem item) return;

            Type? pageType = item.Tag?.ToString() switch
            {
                "connections" => typeof(MainPage),
                "settings"    => typeof(SettingsPage),
                _             => null
            };

            if (pageType != null)
            {
                ContentFrame.Navigate(pageType);
                ContentFrame.BackStack.Clear();
            }
        }

        // ── Theme System (ported from AnywhereWinUI) ─────────────────────────

        public event Action<ElementTheme>? ThemeChanged;

        public void SetTheme(ElementTheme theme)
        {
            _currentTheme = theme;
            if (this.Content is FrameworkElement root)
                root.RequestedTheme = theme;
            UpdateThemeToggleIcon(theme);
            ThemeChanged?.Invoke(theme);
        }

        private ElementTheme GetActualTheme()
        {
            if (_currentTheme != ElementTheme.Default) return _currentTheme;
            return Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark : ElementTheme.Light;
        }

        private bool _isThemeTransitioning;

        private async void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isThemeTransitioning) return;
            _isThemeTransitioning = true;

            var actualTheme = GetActualTheme();
            var newTheme = actualTheme == ElementTheme.Dark
                ? ElementTheme.Light : ElementTheme.Dark;

            // Fire icon spin+bounce animation in parallel
            _ = AnimateThemeIconAsync();

            if (this.Content is FrameworkElement rootElement
                && ThemeTransitionOverlay != null)
            {
                var renderTargetBitmap = new RenderTargetBitmap();
                try
                {
                    await renderTargetBitmap.RenderAsync(rootElement);
                    ThemeTransitionImage.Source = renderTargetBitmap;

                    ThemeTransitionBackground.Background =
                        actualTheme == ElementTheme.Dark
                        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(255, 32, 32, 32))
                        : new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(255, 243, 243, 243));

                    ThemeTransitionOverlay.Visibility = Visibility.Visible;
                }
                catch
                {
                    SetTheme(newTheme);
                    await SaveThemeAsync(newTheme);
                    _isThemeTransitioning = false;
                    return;
                }

                // Circular reveal from theme button position
                Windows.Foundation.Point buttonCenter = new(rootElement.ActualWidth - 40, 40);
                try
                {
                    var t = ThemeToggleButton.TransformToVisual(rootElement);
                    buttonCenter = t.TransformPoint(new Windows.Foundation.Point(
                        ThemeToggleButton.ActualWidth / 2, ThemeToggleButton.ActualHeight / 2));
                }
                catch { }

                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                    .GetElementVisual(ContentWrapper);
                var compositor = visual.Compositor;

                var ellipseGeometry = compositor.CreateEllipseGeometry();
                ellipseGeometry.Center = new System.Numerics.Vector2(
                    (float)buttonCenter.X, (float)buttonCenter.Y);
                ellipseGeometry.Radius = new System.Numerics.Vector2(0, 0);
                visual.Clip = compositor.CreateGeometricClip(ellipseGeometry);

                ContentWrapper.Background = newTheme == ElementTheme.Dark
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(255, 32, 32, 32))
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(255, 243, 243, 243));

                SetTheme(newTheme);
                await SaveThemeAsync(newTheme);
                await Task.Delay(30);

                float w = (float)rootElement.ActualWidth;
                float h = (float)rootElement.ActualHeight;
                float maxX = Math.Max((float)buttonCenter.X, w - (float)buttonCenter.X);
                float maxY = Math.Max((float)buttonCenter.Y, h - (float)buttonCenter.Y);
                float maxRadius = (float)Math.Sqrt(maxX * maxX + maxY * maxY);

                var easing = compositor.CreateCubicBezierEasingFunction(
                    new System.Numerics.Vector2(0.25f, 0.85f),
                    new System.Numerics.Vector2(0.15f, 1.0f));

                var animX = compositor.CreateScalarKeyFrameAnimation();
                animX.InsertKeyFrame(1f, maxRadius, easing);
                animX.Duration = TimeSpan.FromMilliseconds(1300);

                var animY = compositor.CreateScalarKeyFrameAnimation();
                animY.InsertKeyFrame(1f, maxRadius, easing);
                animY.Duration = TimeSpan.FromMilliseconds(1300);

                var batch = compositor.CreateScopedBatch(
                    Microsoft.UI.Composition.CompositionBatchTypes.Animation);
                ellipseGeometry.StartAnimation("Radius.X", animX);
                ellipseGeometry.StartAnimation("Radius.Y", animY);

                batch.Completed += (_, _) =>
                {
                    visual.Clip = null;
                    ContentWrapper.Background = null;
                    ThemeTransitionOverlay.Visibility = Visibility.Collapsed;
                    ThemeTransitionImage.Source = null;
                    _isThemeTransitioning = false;
                };
                batch.End();
            }
            else
            {
                SetTheme(newTheme);
                await SaveThemeAsync(newTheme);
                _isThemeTransitioning = false;
            }
        }

        private async Task SaveThemeAsync(ElementTheme theme)
        {
            var settings = await _storage.LoadSettingsAsync();
            settings.ColorScheme = theme == ElementTheme.Light ? "Default Light" : "One Dark";
            await _storage.SaveSettingsAsync(settings);
        }

        private async Task AnimateThemeIconAsync()
        {
            if (ThemeToggleIcon == null) return;
            var iconVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview
                .GetElementVisual(ThemeToggleIcon);
            var compositor = iconVisual.Compositor;

            float cx = ThemeToggleIcon.ActualWidth  > 0 ? (float)(ThemeToggleIcon.ActualWidth  / 2) : 8f;
            float cy = ThemeToggleIcon.ActualHeight > 0 ? (float)(ThemeToggleIcon.ActualHeight / 2) : 8f;
            iconVisual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

            var easeIn = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.4f, 0f),
                new System.Numerics.Vector2(1f, 1f));

            // Phase 1: exit (shrink + rotate 180°)
            var exitBatch = compositor.CreateScopedBatch(
                Microsoft.UI.Composition.CompositionBatchTypes.Animation);

            var exitSX = compositor.CreateScalarKeyFrameAnimation();
            exitSX.InsertKeyFrame(0f, 1f); exitSX.InsertKeyFrame(1f, 0f, easeIn);
            exitSX.Duration = TimeSpan.FromMilliseconds(180);

            var exitSY = compositor.CreateScalarKeyFrameAnimation();
            exitSY.InsertKeyFrame(0f, 1f); exitSY.InsertKeyFrame(1f, 0f, easeIn);
            exitSY.Duration = TimeSpan.FromMilliseconds(180);

            var exitRot = compositor.CreateScalarKeyFrameAnimation();
            exitRot.InsertKeyFrame(0f, 0f); exitRot.InsertKeyFrame(1f, 180f, easeIn);
            exitRot.Duration = TimeSpan.FromMilliseconds(180);

            iconVisual.StartAnimation("Scale.X", exitSX);
            iconVisual.StartAnimation("Scale.Y", exitSY);
            iconVisual.StartAnimation("RotationAngleInDegrees", exitRot);

            var exitTcs = new TaskCompletionSource<bool>();
            exitBatch.Completed += (_, _) => exitTcs.TrySetResult(true);
            exitBatch.End();
            await exitTcs.Task;

            iconVisual.RotationAngleInDegrees = 180f;
            iconVisual.Scale = new System.Numerics.Vector3(0f, 0f, 1f);

            var easeOut = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0f, 0f),
                new System.Numerics.Vector2(0.2f, 1f));

            // Phase 2: enter (spring bounce + rotate to 360°)
            var enterBatch = compositor.CreateScopedBatch(
                Microsoft.UI.Composition.CompositionBatchTypes.Animation);

            var enterSX = compositor.CreateScalarKeyFrameAnimation();
            enterSX.InsertKeyFrame(0.00f, 0f);
            enterSX.InsertKeyFrame(0.55f, 1.25f);
            enterSX.InsertKeyFrame(0.75f, 0.92f);
            enterSX.InsertKeyFrame(1.00f, 1f);
            enterSX.Duration = TimeSpan.FromMilliseconds(400);

            var enterSY = compositor.CreateScalarKeyFrameAnimation();
            enterSY.InsertKeyFrame(0.00f, 0f);
            enterSY.InsertKeyFrame(0.55f, 1.25f);
            enterSY.InsertKeyFrame(0.75f, 0.92f);
            enterSY.InsertKeyFrame(1.00f, 1f);
            enterSY.Duration = TimeSpan.FromMilliseconds(400);

            var enterRot = compositor.CreateScalarKeyFrameAnimation();
            enterRot.InsertKeyFrame(0f, 180f); enterRot.InsertKeyFrame(1f, 360f, easeOut);
            enterRot.Duration = TimeSpan.FromMilliseconds(400);

            iconVisual.StartAnimation("Scale.X", enterSX);
            iconVisual.StartAnimation("Scale.Y", enterSY);
            iconVisual.StartAnimation("RotationAngleInDegrees", enterRot);

            var enterTcs = new TaskCompletionSource<bool>();
            enterBatch.Completed += (_, _) => enterTcs.TrySetResult(true);
            enterBatch.End();
            await enterTcs.Task;

            iconVisual.RotationAngleInDegrees = 0f;
            iconVisual.Scale = new System.Numerics.Vector3(1f, 1f, 1f);
        }

        private void UpdateThemeToggleIcon(ElementTheme theme)
        {
            if (ThemeToggleIcon == null) return;
            var actual = theme == ElementTheme.Default
                ? (Application.Current.RequestedTheme == ApplicationTheme.Dark
                    ? ElementTheme.Dark : ElementTheme.Light)
                : theme;
            ThemeToggleIcon.Glyph = actual == ElementTheme.Dark ? "\uE706" : "\uE708";
            if (ThemeToggleButton != null)
                ToolTipService.SetToolTip(ThemeToggleButton,
                    actual == ElementTheme.Dark ? "切换至浅色模式" : "切换至深色模式");
        }

        // ── Header visibility with fade animation ────────────────────────────

        private void UpdateHeaderVisibility(bool isOpen, bool animate = true)
        {
            FadeVisual(ThemeToggleButton, isOpen ? 1f : 0f, animate ? 250 : 0);
            FadeVisual(LogoStackPanel,    isOpen ? 0f : 1f, animate ? 250 : 0);
        }

        private static void FadeVisual(UIElement? element, float targetOpacity, double durationMs)
        {
            if (element == null) return;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
            
            if (durationMs <= 0)
            {
                visual.Opacity = targetOpacity;
                element.Visibility = targetOpacity > 0f ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            if (targetOpacity > 0f) element.Visibility = Visibility.Visible;

            var compositor = visual.Compositor;
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, targetOpacity);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);

            var batch = compositor.CreateScopedBatch(
                Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            visual.StartAnimation("Opacity", anim);
            batch.Completed += (_, _) =>
            {
                if (targetOpacity == 0f) element.Visibility = Visibility.Collapsed;
            };
            batch.End();
        }

        // ── Nav icon hover animations (ported from AnywhereWinUI) ────────────

        // Connections: Scale pulse
        private void ConnectionsNavItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            AnimateNavIconScale(ConnectionsNavIcon, 1.22f, 300);
        }
        private void ConnectionsNavItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            AnimateNavIconScale(ConnectionsNavIcon, 1f, 250);
        }

        // Settings: Gear 180° spin
        private void SettingsNavItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            AnimateNavIconRotation(SettingsNavIcon, 180f);
        }
        private void SettingsNavItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            AnimateNavIconRotation(SettingsNavIcon, 0f);
        }

        private static void AnimateNavIconScale(FontIcon? icon, float targetScale, double durationMs)
        {
            if (icon == null) return;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var compositor = visual.Compositor;

            visual.StopAnimation("Scale.X");
            visual.StopAnimation("Scale.Y");

            float cx = icon.ActualWidth  > 0 ? (float)(icon.ActualWidth  / 2) : 8f;
            float cy = icon.ActualHeight > 0 ? (float)(icon.ActualHeight / 2) : 8f;
            visual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

            var ease = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f));

            var sx = compositor.CreateScalarKeyFrameAnimation();
            sx.InsertKeyFrame(1f, targetScale, ease);
            sx.Duration = TimeSpan.FromMilliseconds(durationMs);

            var sy = compositor.CreateScalarKeyFrameAnimation();
            sy.InsertKeyFrame(1f, targetScale, ease);
            sy.Duration = TimeSpan.FromMilliseconds(durationMs);

            visual.StartAnimation("Scale.X", sx);
            visual.StartAnimation("Scale.Y", sy);
        }

        private static void AnimateNavIconRotation(FontIcon? icon, float targetAngle)
        {
            if (icon == null) return;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var compositor = visual.Compositor;

            float cx = icon.ActualWidth  > 0 ? (float)(icon.ActualWidth  / 2) : 8f;
            float cy = icon.ActualHeight > 0 ? (float)(icon.ActualHeight / 2) : 8f;
            visual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

            var ease = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f));

            var rot = compositor.CreateScalarKeyFrameAnimation();
            rot.InsertKeyFrame(1f, targetAngle, ease);
            rot.Duration = TimeSpan.FromMilliseconds(400);

            visual.StartAnimation("RotationAngleInDegrees", rot);
        }

        // ── Tray Integration ─────────────────────────────────────────────────

        private void ConfigureTray()
        {
            _windowManager.IsVisibleInTray = true;
            _windowManager.TrayIconSelected += (_, _) => RestoreFromTray();
            _windowManager.TrayIconContextMenu += (_, e) =>
            {
                var flyout = new MenuFlyout();

                var openItem = new MenuFlyoutItem { Text = "显示 SwellSSH" };
                openItem.Click += (_, _) => RestoreFromTray();
                flyout.Items.Add(openItem);

                flyout.Items.Add(new MenuFlyoutSeparator());

                var exitItem = new MenuFlyoutItem { Text = "退出程序" };
                exitItem.Click += (_, _) => ExitApplication();
                flyout.Items.Add(exitItem);

                e.Flyout = flyout;
            };

            this.AppWindow.Closing += (_, args) =>
            {
                args.Cancel = true;
                HideToTray();
            };
        }

        private void HideToTray()
        {
            if (_isHiddenToTray) return;
            _isHiddenToTray = true;
            this.AppWindow.IsShownInSwitchers = false;
            this.AppWindow.Hide();
            ReleaseUiResources();
        }

        private void RestoreFromTray()
        {
            _isHiddenToTray = false;
            this.AppWindow.IsShownInSwitchers = true;
            this.Activate();
            this.AppWindow.Show();
            this.AppWindow.MoveInZOrderAtTop();
        }

        private void ExitApplication()
        {
            // TODO Phase 5: gracefully close all active SSH sessions
            this.AppWindow.Closing -= null;
            Application.Current.Exit();
        }

        // ── Memory Optimization ──────────────────────────────────────────────

        private static void ReleaseUiResources()
        {
            Task.Run(() =>
            {
                try
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                        System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized,
                        blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();

                    using var process = Process.GetCurrentProcess();
                    SetProcessWorkingSetSize(process.Handle, (IntPtr)(-1), (IntPtr)(-1));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Tray] ReleaseUiResources: {ex.Message}");
                }
            });
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

        // ── Utilities ────────────────────────────────────────────────────────

        private void SetIconSafe()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray-icon.ico");
                if (File.Exists(path)) this.AppWindow.SetIcon(path);
            }
            catch { }
        }
    }
}
