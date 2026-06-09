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

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            File.AppendAllText(@"d:\test\SwellSSH\debug.log", $"[{DateTime.Now}] OnLaunched starting...\n");
            // Single-instance guard
            const string mutexName = "SwellSSH_SingleInstance_Global_V3";
            _singleInstanceMutex = new System.Threading.Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                File.AppendAllText(@"d:\test\SwellSSH\debug.log", $"[{DateTime.Now}] Exiting due to mutex\n");
                Environment.Exit(0);
                return;
            }

            File.AppendAllText(@"d:\test\SwellSSH\debug.log", $"[{DateTime.Now}] Creating MainWindow...\n");
            try {
                _window = new MainWindow();
                File.AppendAllText(@"d:\test\SwellSSH\debug.log", $"[{DateTime.Now}] Activating MainWindow...\n");
                _window.Activate();
                _window.AppWindow.Show();
                _window.AppWindow.MoveInZOrderAtTop();
                File.AppendAllText(@"d:\test\SwellSSH\debug.log", $"[{DateTime.Now}] OnLaunched finished.\n");
            } catch (Exception ex) {
                File.AppendAllText(@"d:\test\SwellSSH\debug.log", $"[{DateTime.Now}] EXCEPTION in OnLaunched: {ex}\n");
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
