using System;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;

namespace SwellSSH
{
    public partial class App : Application
    {
        private static System.Threading.Mutex? _singleInstanceMutex;
        private Window? _window;

        public static new App Current => (App)Application.Current;
        public IntPtr MainWindowHandle => _window != null
            ? WinRT.Interop.WindowNative.GetWindowHandle(_window)
            : IntPtr.Zero;

        public App()
        {
            this.InitializeComponent();

            this.UnhandledException += (s, e) =>
            {
                LogCrash(e.Exception, e.Message);
                e.Handled = true; // Non-fatal UI exceptions should not kill the app
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                LogCrash(e.ExceptionObject as Exception, $"AppDomain: {e.ExceptionObject}");

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogCrash(e.Exception, "UnobservedTaskException");
                e.SetObserved();
            };
        }

        private static void AppendDebugLog(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug.log"), $"[{DateTime.Now}] {message}\n");
            }
            catch { }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            AppendDebugLog("OnLaunched starting...");
            // Single-instance guard
            const string mutexName = "SwellSSH_SingleInstance_Global_V3";
            _singleInstanceMutex = new System.Threading.Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                AppendDebugLog("Exiting due to mutex");
                Environment.Exit(0);
                return;
            }

            // Clean up old updater staging directories
            new SwellSSH.Services.AppUpdateService().CleanupOldStagingDirs();

            AppendDebugLog("Creating MainWindow...");
            try {
                _window = new MainWindow();
                AppendDebugLog("Activating MainWindow...");
                _window.Activate();
                _window.AppWindow.Show();
                _window.AppWindow.MoveInZOrderAtTop();
                AppendDebugLog("OnLaunched finished.");
            } catch (Exception ex) {
                AppendDebugLog($"EXCEPTION in OnLaunched: {ex}");
            }
        }

        private static void LogCrash(Exception? ex, string message)
        {
            try
            {
                string logPath = Path.Combine(AppContext.BaseDirectory, "crash_log.txt");
                var sb = new StringBuilder();
                sb.AppendLine("==================================================");
                sb.AppendLine($"[Crash Timestamp] {DateTime.Now}");
                sb.AppendLine($"[Message] {message}");
                if (ex != null)
                {
                    sb.AppendLine($"[Exception] {ex.GetType().FullName}");
                    sb.AppendLine(ex.ToString());
                    if (ex.InnerException != null)
                        sb.AppendLine($"[Inner] {ex.InnerException}");
                }
                sb.AppendLine("==================================================");
                File.AppendAllText(logPath, sb.ToString());
            }
            catch { }
        }
    }
}
