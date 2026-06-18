using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using SwellSSH.Models;
using Windows.UI;

namespace SwellSSH.Services
{
    /// <summary>Loads bundled themes and optional user overrides from JSON.</summary>
    public sealed class TerminalThemeService
    {
        public static TerminalThemeService Instance { get; } = new();

        private readonly List<TerminalTheme> _themes = new();
        public IReadOnlyList<TerminalTheme> Themes => _themes;
        public string UserThemeFilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SwellSSH", "terminal-themes.json");

        private TerminalThemeService() => Reload();

        public void Reload()
        {
            _themes.Clear();
            MergeFile(Path.Combine(AppContext.BaseDirectory, "Assets", "terminal-themes.json"));
            MergeFile(UserThemeFilePath);
        }

        public TerminalTheme? Find(string? name) =>
            _themes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        private void MergeFile(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                var loaded = DeserializeThemes(File.ReadAllText(path));
                foreach (var theme in loaded.Where(IsValid))
                {
                    var existing = _themes.FindIndex(t =>
                        string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0) _themes[existing] = theme;
                    else _themes.Add(theme);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Themes] Failed to load {path}: {ex.Message}");
            }
        }

        private static bool IsValid(TerminalTheme theme) =>
            !string.IsNullOrWhiteSpace(theme.Name) && theme.AnsiColors.Count == 16;

        public async Task SaveUserThemeAsync(TerminalTheme theme)
        {
            if (!IsValid(theme))
                throw new ArgumentException("A terminal theme requires a name and exactly 16 ANSI colors.", nameof(theme));

            var userThemes = new List<TerminalTheme>();
            if (File.Exists(UserThemeFilePath))
            {
                try
                {
                    userThemes = DeserializeThemes(await File.ReadAllTextAsync(UserThemeFilePath));
                }
                catch { }
            }

            var existing = userThemes.FindIndex(t =>
                string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) userThemes[existing] = theme;
            else userThemes.Add(theme);

            Directory.CreateDirectory(Path.GetDirectoryName(UserThemeFilePath)!);
            await File.WriteAllTextAsync(UserThemeFilePath, JsonSerializer.Serialize(userThemes,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
            Reload();
        }

        private static List<TerminalTheme> DeserializeThemes(string json)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<TerminalTheme>>(root.GetRawText(), options) ?? new();
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("schemes", out var schemes) && schemes.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<TerminalTheme>>(schemes.GetRawText(), options) ?? new();
            if (root.ValueKind == JsonValueKind.Object)
            {
                var single = JsonSerializer.Deserialize<TerminalTheme>(root.GetRawText(), options);
                return single == null ? new() : new() { single };
            }
            return new();
        }

        public static Color ParseColor(string value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var hex = value.Trim().TrimStart('#');
            try
            {
                return hex.Length switch
                {
                    6 => Color.FromArgb(255,
                        Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16)),
                    8 => Color.FromArgb(Convert.ToByte(hex[0..2], 16),
                        Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16)),
                    _ => fallback
                };
            }
            catch { return fallback; }
        }
    }
}
