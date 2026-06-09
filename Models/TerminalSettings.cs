namespace SwellSSH.Models
{
    /// <summary>
    /// Global terminal appearance and behavior settings.
    /// Persisted alongside connection profiles.
    /// </summary>
    public class TerminalSettings
    {
        public string FontFamily { get; set; } = "Consolas";
        public double FontSize { get; set; } = 16.0;

        /// <summary>One Dark | Solarized Dark | Dracula | Catppuccin Mocha | Tokyo Night | Nord | Gruvbox Dark | Default Light</summary>
        public string ColorScheme { get; set; } = "One Dark";

        /// <summary>Block | Underline | Bar</summary>
        public string CursorStyle { get; set; } = "Block";

        public bool CursorBlink { get; set; } = true;

        /// <summary>Mica | Acrylic | None</summary>
        public string BackdropType { get; set; } = "Mica";

        /// <summary>0.0 (transparent) to 1.0 (opaque)</summary>
        public double BackgroundOpacity { get; set; } = 0.95;

        public bool MinimizeOnClose { get; set; } = true;

        public int ScrollbackLines { get; set; } = 1000;

        public static event System.Action<TerminalSettings>? GlobalSettingsChanged;
        public static void NotifyGlobalSettingsChanged(TerminalSettings settings)
        {
            GlobalSettingsChanged?.Invoke(settings);
        }
    }
}
