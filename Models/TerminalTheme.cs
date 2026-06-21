using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SwellSSH.Models
{
    public sealed class TerminalTheme
    {
        public string Name { get; set; } = "";
        public string Black { get; set; } = "#0C0C0C";
        public string Red { get; set; } = "#C50F1F";
        public string Green { get; set; } = "#13A10E";
        public string Yellow { get; set; } = "#C19C00";
        public string Blue { get; set; } = "#0037DA";
        public string Purple { get; set; } = "#881798";
        public string Cyan { get; set; } = "#3A96DD";
        public string White { get; set; } = "#CCCCCC";
        public string BrightBlack { get; set; } = "#767676";
        public string BrightRed { get; set; } = "#E74856";
        public string BrightGreen { get; set; } = "#16C60C";
        public string BrightYellow { get; set; } = "#F9F1A5";
        public string BrightBlue { get; set; } = "#3B78FF";
        public string BrightPurple { get; set; } = "#B4009E";
        public string BrightCyan { get; set; } = "#61D6D6";
        public string BrightWhite { get; set; } = "#F2F2F2";
        public string Background { get; set; } = "#0C0C0C";
        public string Foreground { get; set; } = "#CCCCCC";
        public string SelectionBackground { get; set; } = "#264F78";
        public string CursorColor { get; set; } = "#CCCCCC";

        [JsonIgnore]
        public List<string> AnsiColors
        {
            get => new()
            {
                Black, Red, Green, Yellow, Blue, Purple, Cyan, White,
                BrightBlack, BrightRed, BrightGreen, BrightYellow,
                BrightBlue, BrightPurple, BrightCyan, BrightWhite
            };
            set
            {
                if (value == null || value.Count != 16) return;
                Black = value[0]; Red = value[1]; Green = value[2]; Yellow = value[3];
                Blue = value[4]; Purple = value[5]; Cyan = value[6]; White = value[7];
                BrightBlack = value[8]; BrightRed = value[9]; BrightGreen = value[10]; BrightYellow = value[11];
                BrightBlue = value[12]; BrightPurple = value[13]; BrightCyan = value[14]; BrightWhite = value[15];
            }
        }

        [JsonPropertyName("ansiColors")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? LegacyAnsiColors
        {
            get => null;
            set { if (value != null) AnsiColors = value; }
        }
    }
}
