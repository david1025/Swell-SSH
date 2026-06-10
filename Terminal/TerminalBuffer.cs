using System;
using System.Collections.Generic;

namespace SwellSSH.Terminal
{
    public struct TerminalCell
    {
        public char Char;
        public uint FgColor; // 0xRRGGBB format or special index
        public uint BgColor;
        public bool IsBold;
        public bool IsItalic;
        public bool IsUnderline;

        public const uint DefaultFg = 0xFFFFFFFF;
        public const uint DefaultBg = 0xFF000000;
        public const uint IndexedColorMask = 0xFE000000; // Special marker for ANSI indexed 0-255 colors
        public const uint SelectionBgMask = 0xFD000000; // Special marker for selected text background
    }

    public class TerminalRow
    {
        public TerminalCell[] Cells;

        public TerminalRow(int cols)
        {
            Cells = new TerminalCell[cols];
            Clear(0, cols);
        }

        public void Clear(int startCol, int count)
        {
            for (int i = startCol; i < startCol + count && i < Cells.Length; i++)
            {
                Cells[i] = new TerminalCell
                {
                    Char = ' ',
                    FgColor = TerminalCell.DefaultFg,
                    BgColor = TerminalCell.DefaultBg
                };
            }
        }
    }

    /// <summary>
    /// Maintains the 2D grid of terminal cells and cursor state.
    /// Implements ITerminalActionHandler to be driven by VtParser.
    /// </summary>
    public sealed class TerminalBuffer : ITerminalActionHandler
    {
        public int Rows { get; private set; }
        public int Cols { get; private set; }

        public List<TerminalRow> Lines { get; } = new();
        public List<TerminalRow> Scrollback { get; } = new();
        public int MaxScrollback { get; set; } = 1000;

        public int CursorX { get; private set; }
        public int CursorY { get; private set; }

        // Current graphic rendition state
        private TerminalCell _currentAttr = new()
        {
            FgColor = TerminalCell.DefaultFg,
            BgColor = TerminalCell.DefaultBg
        };

        public event Action? BufferChanged;
        public event Action<string>? TitleChanged;

        public TerminalBuffer(int cols, int rows)
        {
            Resize(cols, rows);
        }

        public void Resize(int cols, int rows)
        {
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;

            Cols = cols;
            Rows = rows;

            // Ensure we have enough rows
            while (Lines.Count < rows)
            {
                Lines.Add(new TerminalRow(cols));
            }

            // Remove excess rows if shrinking
            while (Lines.Count > rows)
            {
                Lines.RemoveAt(Lines.Count - 1);
            }

            // Ensure all rows have correct width
            foreach (var line in Lines)
            {
                if (line.Cells.Length != cols)
                {
                    var newCells = new TerminalCell[cols];
                    int copyLen = Math.Min(line.Cells.Length, cols);
                    Array.Copy(line.Cells, newCells, copyLen);
                    for (int i = copyLen; i < cols; i++)
                    {
                        newCells[i] = new TerminalCell { Char = ' ', FgColor = TerminalCell.DefaultFg, BgColor = TerminalCell.DefaultBg };
                    }
                    line.Cells = newCells;
                }
            }

            if (CursorX >= Cols) CursorX = Cols - 1;
            if (CursorY >= Rows) CursorY = Rows - 1;

            BufferChanged?.Invoke();
        }

        public string GetText(int startX, int startY, int endX, int endY)
        {
            if (startY < 0) startY = 0;
            if (endY >= Rows) endY = Rows - 1;

            var sb = new System.Text.StringBuilder();

            for (int y = startY; y <= endY; y++)
            {
                var row = Lines[y];
                int sX = (y == startY) ? Math.Max(0, startX) : 0;
                int eX = (y == endY) ? Math.Min(Cols - 1, endX) : Cols - 1;

                if (sX > eX) continue;

                var lineSb = new System.Text.StringBuilder();
                for (int x = sX; x <= eX; x++)
                {
                    char c = row.Cells[x].Char;
                    if (c == '\0') continue; // Skip wide char filler
                    lineSb.Append(c);
                }

                // If not the last line of selection, strip trailing spaces and append newline
                if (y < endY)
                {
                    sb.AppendLine(lineSb.ToString().TrimEnd());
                }
                else
                {
                    sb.Append(lineSb.ToString());
                }
            }

            return sb.ToString();
        }

        // ── ITerminalActionHandler Implementation ─────────────────────────────

        public void Print(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool isSurrogate = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                int w = isSurrogate ? 2 : WcWidth(c);

                if (w == 2 && CursorX == Cols - 1)
                {
                    Lines[CursorY].Cells[CursorX] = _currentAttr;
                    Lines[CursorY].Cells[CursorX].Char = ' ';
                    CursorX = 0;
                    CursorDown();
                }
                else if (CursorX >= Cols)
                {
                    CursorX = 0;
                    CursorDown();
                }

                Lines[CursorY].Cells[CursorX] = _currentAttr;
                Lines[CursorY].Cells[CursorX].Char = c;
                CursorX++;

                if (isSurrogate)
                {
                    i++;
                    Lines[CursorY].Cells[CursorX] = _currentAttr;
                    Lines[CursorY].Cells[CursorX].Char = text[i];
                    CursorX++;
                }
                else if (w == 2)
                {
                    Lines[CursorY].Cells[CursorX] = _currentAttr;
                    Lines[CursorY].Cells[CursorX].Char = '\0';
                    CursorX++;
                }
            }
            BufferChanged?.Invoke();
        }

        private int WcWidth(char c)
        {
            if (c >= 0x1100 &&
                (c <= 0x115F ||
                 c == 0x2329 || c == 0x232A ||
                 (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) ||
                 (c >= 0xAC00 && c <= 0xD7A3) ||
                 (c >= 0xF900 && c <= 0xFAFF) ||
                 (c >= 0xFE10 && c <= 0xFE19) ||
                 (c >= 0xFE30 && c <= 0xFE6F) ||
                 (c >= 0xFF00 && c <= 0xFF60) ||
                 (c >= 0xFFE0 && c <= 0xFFE6)))
            {
                return 2;
            }
            // Some emojis in BMP
            if (c >= 0x231A && c <= 0x2B55)
            {
                return 2; 
            }
            return 1;
        }

        public void ExecuteControlCharacter(byte b)
        {
            switch (b)
            {
                case 0x08: // BS (Backspace)
                    if (CursorX > 0) CursorX--;
                    break;
                case 0x09: // HT (Tab)
                    CursorX = (CursorX + 8) / 8 * 8;
                    if (CursorX >= Cols) CursorX = Cols - 1;
                    break;
                case 0x0A: // LF (Line Feed)
                case 0x0B: // VT
                case 0x0C: // FF
                    CursorDown();
                    break;
                case 0x0D: // CR (Carriage Return)
                    CursorX = 0;
                    break;
            }
            BufferChanged?.Invoke();
        }

        public void EscDispatch(char action)
        {
            switch (action)
            {
                case 'M': // Reverse Index (scroll up)
                    if (CursorY == 0)
                    {
                        ScrollUp();
                    }
                    else
                    {
                        CursorY--;
                    }
                    break;
                // Add more ESC sequences (like save/restore cursor) as needed
            }
            BufferChanged?.Invoke();
        }

        public void CsiDispatch(char action, int[] parameters, bool hasQuestionMark)
        {
            if (hasQuestionMark)
            {
                // DEC Private Mode sequences (e.g., CSI ? 25 h for cursor, CSI ? 1049 h for alt buffer)
                // We currently ignore these, but we MUST return early so they don't trigger standard CSI logic.
                return;
            }

            int p1 = parameters.Length > 0 ? parameters[0] : 0;
            int p2 = parameters.Length > 1 ? parameters[1] : 0;

            switch (action)
            {
                case 'A': // CUU - Cursor Up
                    CursorY -= Math.Max(1, p1);
                    if (CursorY < 0) CursorY = 0;
                    break;
                case 'B': // CUD - Cursor Down
                    CursorY += Math.Max(1, p1);
                    if (CursorY >= Rows) CursorY = Rows - 1;
                    break;
                case 'C': // CUF - Cursor Forward
                    CursorX += Math.Max(1, p1);
                    if (CursorX >= Cols) CursorX = Cols - 1;
                    break;
                case 'D': // CUB - Cursor Back
                    CursorX -= Math.Max(1, p1);
                    if (CursorX < 0) CursorX = 0;
                    break;
                case 'H': // CUP - Cursor Position
                case 'f': // HVP
                    CursorY = Math.Max(0, (p1 == 0 ? 1 : p1) - 1);
                    CursorX = Math.Max(0, (p2 == 0 ? 1 : p2) - 1);
                    if (CursorY >= Rows) CursorY = Rows - 1;
                    if (CursorX >= Cols) CursorX = Cols - 1;
                    break;
                case 'J': // ED - Erase in Display
                    if (p1 == 0) // Below
                    {
                        Lines[CursorY].Clear(CursorX, Cols - CursorX);
                        for (int i = CursorY + 1; i < Rows; i++) Lines[i].Clear(0, Cols);
                    }
                    else if (p1 == 1) // Above
                    {
                        for (int i = 0; i < CursorY; i++) Lines[i].Clear(0, Cols);
                        Lines[CursorY].Clear(0, CursorX + 1);
                    }
                    else if (p1 == 2) // All
                    {
                        for (int i = 0; i < Rows; i++) Lines[i].Clear(0, Cols);
                        CursorX = 0; CursorY = 0;
                    }
                    break;
                case 'K': // EL - Erase in Line
                    if (p1 == 0) // Right
                        Lines[CursorY].Clear(CursorX, Cols - CursorX);
                    else if (p1 == 1) // Left
                        Lines[CursorY].Clear(0, CursorX + 1);
                    else if (p1 == 2) // All
                        Lines[CursorY].Clear(0, Cols);
                    break;
                case 'm': // SGR - Select Graphic Rendition
                    HandleSgr(parameters);
                    break;
            }
            BufferChanged?.Invoke();
        }

        public void OscDispatch(int command, string payload)
        {
            if (command == 0 || command == 1 || command == 2)
            {
                TitleChanged?.Invoke(payload);
            }
        }

        // ── Internal Helpers ──────────────────────────────────────────────────

        private void CursorDown()
        {
            CursorY++;
            if (CursorY >= Rows)
            {
                CursorY = Rows - 1;
                ScrollDown();
            }
        }

        private void ScrollDown()
        {
            // Remove top row, add empty row at bottom
            var topRow = Lines[0];
            Lines.RemoveAt(0);
            Lines.Add(new TerminalRow(Cols));

            Scrollback.Add(topRow);
            if (Scrollback.Count > MaxScrollback)
            {
                int removeCount = Scrollback.Count - MaxScrollback;
                Scrollback.RemoveRange(0, removeCount);
            }
        }

        private void ScrollUp()
        {
            // Remove bottom row, add empty row at top
            Lines.RemoveAt(Rows - 1);
            Lines.Insert(0, new TerminalRow(Cols));
        }

        private void HandleSgr(int[] parameters)
        {
            if (parameters.Length == 0)
            {
                ResetSgr();
                return;
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                int p = parameters[i];
                if (p == 0) ResetSgr();
                else if (p == 1) _currentAttr.IsBold = true;
                else if (p == 3) _currentAttr.IsItalic = true;
                else if (p == 4) _currentAttr.IsUnderline = true;
                else if (p == 22) _currentAttr.IsBold = false;
                else if (p == 23) _currentAttr.IsItalic = false;
                else if (p == 24) _currentAttr.IsUnderline = false;
                else if (p >= 30 && p <= 37) _currentAttr.FgColor = TerminalCell.IndexedColorMask | (uint)(p - 30); // fg 0-7
                else if (p >= 90 && p <= 97) _currentAttr.FgColor = TerminalCell.IndexedColorMask | (uint)(p - 90 + 8); // fg bright
                else if (p == 39) _currentAttr.FgColor = TerminalCell.DefaultFg;
                else if (p >= 40 && p <= 47) _currentAttr.BgColor = TerminalCell.IndexedColorMask | (uint)(p - 40); // bg 0-7
                else if (p >= 100 && p <= 107) _currentAttr.BgColor = TerminalCell.IndexedColorMask | (uint)(p - 100 + 8); // bg bright
                else if (p == 49) _currentAttr.BgColor = TerminalCell.DefaultBg;
                else if (p == 38 || p == 48) // Extended colors: 38;5;n or 38;2;r;g;b
                {
                    bool isFg = (p == 38);
                    if (i + 2 < parameters.Length && parameters[i + 1] == 5)
                    {
                        // 256 color mode
                        int colorIdx = parameters[i + 2];
                        uint color = TerminalCell.IndexedColorMask | (uint)colorIdx; 
                        if (isFg) _currentAttr.FgColor = color; else _currentAttr.BgColor = color;
                        i += 2;
                    }
                    else if (i + 4 < parameters.Length && parameters[i + 1] == 2)
                    {
                        // True color
                        uint r = (uint)parameters[i + 2];
                        uint g = (uint)parameters[i + 3];
                        uint b = (uint)parameters[i + 4];
                        uint color = (r << 16) | (g << 8) | b;
                        if (isFg) _currentAttr.FgColor = color; else _currentAttr.BgColor = color;
                        i += 4;
                    }
                }
            }
        }

        private void ResetSgr()
        {
            _currentAttr = new TerminalCell
            {
                FgColor = TerminalCell.DefaultFg,
                BgColor = TerminalCell.DefaultBg
            };
        }
    }
}
