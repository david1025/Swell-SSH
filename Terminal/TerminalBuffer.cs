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
        public readonly object SyncRoot = new object();
        public int Rows { get; private set; }
        public int Cols { get; private set; }

        // BUG-01: 改用 O(1) 环形缓冲区，避免 List.RemoveAt(0)/Insert(0) 的 O(n) 开销
        private CircularLineBuffer _lines = new(1);
        /// <summary>只读视图，供渲染层按索引访问。</summary>
        public CircularLineBuffer Lines => _lines;

        public List<TerminalRow> Scrollback { get; } = new();
        public int MaxScrollback { get; set; } = 1000;

        public int CursorX { get; private set; }
        public int CursorY { get; private set; }

        // BUG-09: DECSTBM 滚动区域（CSI r）
        private int _scrollTop    = 0;
        private int _scrollBottom = 0; // 初始化为 Rows-1，在 Resize 里设置

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

            lock (SyncRoot)
            {
                Cols = cols;
                Rows = rows;

                // BUG-01: CircularLineBuffer.Resize 内部一次性重建，O(n) 但只在 resize 时发生
                _lines.Resize(rows, cols);

                // 调整已有行的列宽
                for (int i = 0; i < _lines.Count; i++)
                {
                    var line = _lines[i];
                    if (line.Cells.Length != cols)
                    {
                        var newCells = new TerminalCell[cols];
                        int copyLen = Math.Min(line.Cells.Length, cols);
                        Array.Copy(line.Cells, newCells, copyLen);
                        for (int j = copyLen; j < cols; j++)
                            newCells[j] = new TerminalCell { Char = ' ', FgColor = TerminalCell.DefaultFg, BgColor = TerminalCell.DefaultBg };
                        line.Cells = newCells;
                    }
                }

                if (CursorX >= Cols) CursorX = Cols - 1;
                if (CursorY >= Rows) CursorY = Rows - 1;

                // BUG-09: Resize 时重置滚动区域到全屏
                _scrollTop    = 0;
                _scrollBottom = Rows - 1;
            }

            BufferChanged?.Invoke();
        }

        public string GetText(int startX, int startY, int endX, int endY)
        {
            lock (SyncRoot)
            {
                if (startY < 0) startY = 0;
                // BUG-02: 夹紧到实际行数，防止越界
                if (endY >= _lines.Count) endY = _lines.Count - 1;
                if (endY < startY) return string.Empty;

                var sb = new System.Text.StringBuilder();

                for (int y = startY; y <= endY; y++)
                {
                    var row = _lines[y];
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
                    _lines[CursorY].Cells[CursorX] = _currentAttr;
                    _lines[CursorY].Cells[CursorX].Char = ' ';
                    CursorX = 0;
                    CursorDown();
                }
                else if (CursorX >= Cols)
                {
                    CursorX = 0;
                    CursorDown();
                }

                _lines[CursorY].Cells[CursorX] = _currentAttr;
                _lines[CursorY].Cells[CursorX].Char = c;
                CursorX++;

                if (isSurrogate)
                {
                    i++;
                    _lines[CursorY].Cells[CursorX] = _currentAttr;
                    _lines[CursorY].Cells[CursorX].Char = text[i];
                    CursorX++;
                }
                else if (w == 2)
                {
                    _lines[CursorY].Cells[CursorX] = _currentAttr;
                    _lines[CursorY].Cells[CursorX].Char = '\0';
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
                case 'M': // Reverse Index (scroll up) - BUG-09: 尊重滚动区域
                    if (CursorY == _scrollTop)
                        ScrollUp();
                    else if (CursorY > 0)
                        CursorY--;
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
                        _lines[CursorY].Clear(CursorX, Cols - CursorX);
                        for (int i = CursorY + 1; i < Rows; i++) _lines[i].Clear(0, Cols);
                    }
                    else if (p1 == 1) // Above
                    {
                        for (int i = 0; i < CursorY; i++) _lines[i].Clear(0, Cols);
                        _lines[CursorY].Clear(0, CursorX + 1);
                    }
                    else if (p1 == 2) // All
                    {
                        for (int i = 0; i < Rows; i++) _lines[i].Clear(0, Cols);
                        CursorX = 0; CursorY = 0;
                    }
                    break;
                case 'K': // EL - Erase in Line
                    if (p1 == 0) // Right
                        _lines[CursorY].Clear(CursorX, Cols - CursorX);
                    else if (p1 == 1) // Left
                        _lines[CursorY].Clear(0, CursorX + 1);
                    else if (p1 == 2) // All
                        _lines[CursorY].Clear(0, Cols);
                    break;
                case 'L': // IL - Insert Lines
                {
                    int n = Math.Max(1, p1);
                    for (int i = 0; i < n; i++)
                    {
                        _lines.RemoveLast(); // 移除底部行（超出滚动区）
                        _lines.AddFirst(new TerminalRow(Cols)); // 在光标行前插入空行
                    }
                    break;
                }
                case 'M': // DL - Delete Lines
                {
                    int n = Math.Max(1, p1);
                    for (int i = 0; i < n; i++)
                    {
                        _lines.RemoveFirst();
                        _lines.AddLast(new TerminalRow(Cols));
                    }
                    break;
                }
                case 'r': // DECSTBM - BUG-09: Set Top/Bottom Margins（vim/htop 全屏应用必须）
                    _scrollTop    = (p1 == 0 ? 1 : p1) - 1;
                    _scrollBottom = (p2 == 0 ? Rows : p2) - 1;
                    // 夹紧范围
                    _scrollTop    = Math.Max(0, Math.Min(_scrollTop, Rows - 2));
                    _scrollBottom = Math.Max(_scrollTop + 1, Math.Min(_scrollBottom, Rows - 1));
                    // DECSTBM 后光标移到原点
                    CursorX = 0;
                    CursorY = 0;
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
            // BUG-09: 只在到达滚动区域底部时滚动，而非整个屏幕底部
            if (CursorY > _scrollBottom)
            {
                CursorY = _scrollBottom;
                ScrollDown();
            }
            else if (CursorY >= Rows)
            {
                CursorY = Rows - 1;
            }
        }

        private void ScrollDown()
        {
            // BUG-01: CircularLineBuffer.RemoveFirst/AddLast 均 O(1)
            // BUG-09: 只在滚动区域内操作行，顶部行推入 scrollback
            var topRow = _lines[_scrollTop];

            // 把 _scrollTop..._scrollBottom 范围内的行上移一行
            // 通过移除 _scrollTop 处并在 _scrollBottom 插入空行来模拟
            // 对于全屏滚动区域（最常见），_lines.RemoveFirst + AddLast 是 O(1)
            if (_scrollTop == 0 && _scrollBottom == Rows - 1)
            {
                _lines.RemoveFirst();
                _lines.AddLast(new TerminalRow(Cols));

                // 只有全屏滚动时才推入 scrollback
                Scrollback.Add(topRow);
                if (Scrollback.Count > MaxScrollback)
                {
                    int removeCount = Scrollback.Count - MaxScrollback;
                    Scrollback.RemoveRange(0, removeCount);
                    // BUG-01: TrimExcess 释放 List 底层数组，防止内存碎片
                    Scrollback.TrimExcess();
                }
            }
            else
            {
                // 局部滚动区域：手动移动行（O(region_height)，但 region 通常很小）
                var removed = _lines[_scrollTop];
                for (int i = _scrollTop; i < _scrollBottom; i++)
                    _lines[i] = _lines[i + 1];
                _lines[_scrollBottom] = new TerminalRow(Cols);
                _ = removed; // 局部滚动不进 scrollback
            }
        }

        private void ScrollUp()
        {
            // BUG-09: 在滚动区域内向上滚
            if (_scrollTop == 0 && _scrollBottom == Rows - 1)
            {
                // BUG-01: O(1)
                _lines.RemoveLast();
                _lines.AddFirst(new TerminalRow(Cols));
            }
            else
            {
                for (int i = _scrollBottom; i > _scrollTop; i--)
                    _lines[i] = _lines[i - 1];
                _lines[_scrollTop] = new TerminalRow(Cols);
            }
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
