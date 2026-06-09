using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace SwellSSH.Terminal
{
    public sealed partial class TerminalView : UserControl
    {
        private TerminalSession? _session;
        private CanvasTextFormat? _textFormat;
        private double _charWidth = 9;
        private double _charHeight = 18;
        private int _scrollOffset = 0;

        // Selection state
        private (int x, int y)? _selectionStart;
        private (int x, int y)? _selectionEnd;
        private bool _isSelecting;
        private bool _isLoaded;
        private bool _needsRedraw;
        private bool _isDrawing;
        private bool _hasConnected; // true after the first real-size connection

        // Colors
        private Color _defaultBg = Color.FromArgb(255, 12, 12, 12);
        private Color _defaultFg = Color.FromArgb(255, 204, 204, 204);
        private Color _selectionBg = Color.FromArgb(255, 38, 79, 120);
        
        // Light theme ANSI colors — all tuned for contrast on #FAFAFA background
        private static readonly Color[] LightStandardColors = new Color[16]
        {
            Color.FromArgb(255,  12,  12,  12),  // 0  Black        → near-black (was #F2F2F2, invisible!)
            Color.FromArgb(255, 175,  20,  20),  // 1  Red          → dark red
            Color.FromArgb(255,   0, 130,   0),  // 2  Green        → dark green
            Color.FromArgb(255, 150, 110,   0),  // 3  Yellow       → dark amber/olive
            Color.FromArgb(255,   0,  55, 200),  // 4  Blue         → medium-dark blue
            Color.FromArgb(255, 130,  15, 145),  // 5  Magenta      → dark magenta
            Color.FromArgb(255,   0, 130, 145),  // 6  Cyan         → dark teal
            Color.FromArgb(255, 200, 200, 200),  // 7  White        → light grey (bg placeholder)
            Color.FromArgb(255,  90,  90,  90),  // 8  Bright Black → medium grey
            Color.FromArgb(255, 205,  40,  40),  // 9  Bright Red   → vivid red, readable
            Color.FromArgb(255,   0, 155,   0),  // 10 Bright Green → vivid green, readable
            Color.FromArgb(255, 160, 120,   0),  // 11 Bright Yellow → dark gold (was #F9F1A5, invisible!)
            Color.FromArgb(255,  30,  90, 210),  // 12 Bright Blue  → vivid blue
            Color.FromArgb(255, 160,  20, 170),  // 13 Bright Magenta → vivid purple
            Color.FromArgb(255,   0, 155, 165),  // 14 Bright Cyan  → vivid teal (was #61D6D6, low contrast)
            Color.FromArgb(255,  40,  40,  40)   // 15 Bright White → dark grey (light bg inverted)
        };

        // Dark theme ANSI colors
        private static readonly Color[] DarkStandardColors = new Color[16]
        {
            Color.FromArgb(255, 12, 12, 12),     // 0 Black
            Color.FromArgb(255, 197, 15, 31),    // 1 Red
            Color.FromArgb(255, 19, 161, 14),    // 2 Green
            Color.FromArgb(255, 193, 156, 0),    // 3 Yellow
            Color.FromArgb(255, 0, 55, 218),     // 4 Blue
            Color.FromArgb(255, 136, 23, 152),   // 5 Magenta
            Color.FromArgb(255, 58, 150, 221),   // 6 Cyan
            Color.FromArgb(255, 204, 204, 204),  // 7 White
            Color.FromArgb(255, 118, 118, 118),  // 8 Bright Black
            Color.FromArgb(255, 231, 72, 86),    // 9 Bright Red
            Color.FromArgb(255, 22, 198, 12),    // 10 Bright Green
            Color.FromArgb(255, 249, 241, 165),  // 11 Bright Yellow
            Color.FromArgb(255, 59, 120, 255),   // 12 Bright Blue
            Color.FromArgb(255, 180, 0, 158),    // 13 Bright Magenta
            Color.FromArgb(255, 97, 214, 214),   // 14 Bright Cyan
            Color.FromArgb(255, 242, 242, 242)   // 15 Bright White
        };

        // Catppuccin Mocha ANSI colors
        private static readonly Color[] CatppuccinMochaColors = new Color[16]
        {
            Color.FromArgb(255,  30,  30,  46),  // 0  Black  (Crust)
            Color.FromArgb(255, 243,  97, 120),  // 1  Red    (Red)
            Color.FromArgb(255, 166, 218, 149),  // 2  Green  (Green)
            Color.FromArgb(255, 249, 226, 175),  // 3  Yellow (Yellow)
            Color.FromArgb(255, 137, 180, 250),  // 4  Blue   (Blue)
            Color.FromArgb(255, 245, 194, 231),  // 5  Magenta(Pink)
            Color.FromArgb(255, 148, 226, 213),  // 6  Cyan   (Teal)
            Color.FromArgb(255, 205, 214, 244),  // 7  White  (Text)
            Color.FromArgb(255,  88,  91, 112),  // 8  Bright Black  (Surface1)
            Color.FromArgb(255, 243,  97, 120),  // 9  Bright Red
            Color.FromArgb(255, 166, 218, 149),  // 10 Bright Green
            Color.FromArgb(255, 249, 226, 175),  // 11 Bright Yellow
            Color.FromArgb(255, 137, 180, 250),  // 12 Bright Blue
            Color.FromArgb(255, 245, 194, 231),  // 13 Bright Magenta
            Color.FromArgb(255, 148, 226, 213),  // 14 Bright Cyan
            Color.FromArgb(255, 205, 214, 244)   // 15 Bright White
        };

        // Tokyo Night ANSI colors
        private static readonly Color[] TokyoNightColors = new Color[16]
        {
            Color.FromArgb(255,  26,  27,  38),  // 0  Black
            Color.FromArgb(255, 247,  99,  87),  // 1  Red
            Color.FromArgb(255, 158, 206, 106),  // 2  Green
            Color.FromArgb(255, 224, 175, 104),  // 3  Yellow
            Color.FromArgb(255, 122, 162, 247),  // 4  Blue
            Color.FromArgb(255, 187, 154, 247),  // 5  Magenta
            Color.FromArgb(255, 125, 207, 255),  // 6  Cyan
            Color.FromArgb(255, 169, 177, 214),  // 7  White
            Color.FromArgb(255,  65,  72, 104),  // 8  Bright Black
            Color.FromArgb(255, 247,  99,  87),  // 9  Bright Red
            Color.FromArgb(255, 158, 206, 106),  // 10 Bright Green
            Color.FromArgb(255, 224, 175, 104),  // 11 Bright Yellow
            Color.FromArgb(255, 122, 162, 247),  // 12 Bright Blue
            Color.FromArgb(255, 187, 154, 247),  // 13 Bright Magenta
            Color.FromArgb(255, 125, 207, 255),  // 14 Bright Cyan
            Color.FromArgb(255, 192, 202, 245)   // 15 Bright White
        };

        // Nord ANSI colors
        private static readonly Color[] NordColors = new Color[16]
        {
            Color.FromArgb(255,  46,  52,  64),  // 0  Black   (Nord0)
            Color.FromArgb(255, 191,  97, 106),  // 1  Red     (Nord11)
            Color.FromArgb(255, 163, 190, 140),  // 2  Green   (Nord14)
            Color.FromArgb(255, 235, 203, 139),  // 3  Yellow  (Nord13)
            Color.FromArgb(255, 129, 161, 193),  // 4  Blue    (Nord9)
            Color.FromArgb(255, 180, 142, 173),  // 5  Magenta (Nord15)
            Color.FromArgb(255, 136, 192, 208),  // 6  Cyan    (Nord8)
            Color.FromArgb(255, 216, 222, 233),  // 7  White   (Nord4)
            Color.FromArgb(255,  59,  66,  82),  // 8  Bright Black  (Nord1)
            Color.FromArgb(255, 191,  97, 106),  // 9  Bright Red
            Color.FromArgb(255, 163, 190, 140),  // 10 Bright Green
            Color.FromArgb(255, 235, 203, 139),  // 11 Bright Yellow
            Color.FromArgb(255, 143, 188, 187),  // 12 Bright Blue  (Nord7)
            Color.FromArgb(255, 180, 142, 173),  // 13 Bright Magenta
            Color.FromArgb(255, 136, 192, 208),  // 14 Bright Cyan
            Color.FromArgb(255, 236, 239, 244)   // 15 Bright White  (Nord6)
        };

        // Gruvbox Dark ANSI colors
        private static readonly Color[] GruvboxDarkColors = new Color[16]
        {
            Color.FromArgb(255,  40,  40,  40),  // 0  Black   (bg)
            Color.FromArgb(255, 204,  36,  29),  // 1  Red
            Color.FromArgb(255, 152, 151,  26),  // 2  Green
            Color.FromArgb(255, 215, 153,  33),  // 3  Yellow
            Color.FromArgb(255,  69, 133, 136),  // 4  Blue
            Color.FromArgb(255, 177,  98, 134),  // 5  Magenta (purple)
            Color.FromArgb(255, 104, 157, 106),  // 6  Cyan    (aqua)
            Color.FromArgb(255, 168, 153, 132),  // 7  White   (fg4)
            Color.FromArgb(255, 146, 131, 116),  // 8  Bright Black
            Color.FromArgb(255, 251,  73,  52),  // 9  Bright Red
            Color.FromArgb(255, 184, 187,  38),  // 10 Bright Green
            Color.FromArgb(255, 250, 189,  47),  // 11 Bright Yellow
            Color.FromArgb(255, 131, 165, 152),  // 12 Bright Blue
            Color.FromArgb(255, 211, 134, 155),  // 13 Bright Magenta
            Color.FromArgb(255, 142, 192, 124),  // 14 Bright Cyan
            Color.FromArgb(255, 235, 219, 178)   // 15 Bright White (fg)
        };

        private Color[] _ansiColors = DarkStandardColors;

        private Models.TerminalSettings _settings = new();

        public TerminalView()
        {
            this.InitializeComponent();

            // Handle keyboard input natively on this control
            this.IsTabStop = true;
            this.UseSystemFocusVisuals = false; // Disable default focus rect
            this.CharacterReceived += UIElement_CharacterReceived;
        }

        public void AttachSession(TerminalSession session)
        {
            if (_session != null)
            {
                _session.Buffer.BufferChanged -= RequestRedraw;
            }

            _session = session;
            _session.Buffer.BufferChanged += RequestRedraw;
            RequestRedraw();
            // Note: ConnectAsync is NOT called here.
            // It is called in Canvas_CreateResources after the canvas size is known,
            // so the initial SSH PTY size matches the actual pixel dimensions.
        }

        public void ApplySettings(Models.TerminalSettings settings)
        {
            _settings = settings;
            
            // Apply color scheme simple mapping
            if (settings.ColorScheme == "Default Light")
            {
                _defaultBg = Color.FromArgb(255, 250, 250, 250);
                _defaultFg = Color.FromArgb(255, 50, 50, 50);
                _ansiColors = LightStandardColors;
                _selectionBg = Color.FromArgb(255, 204, 232, 255);
            }
            else if (settings.ColorScheme == "Dracula")
            {
                _defaultBg = Color.FromArgb(255, 40, 42, 54);
                _defaultFg = Color.FromArgb(255, 248, 248, 242);
                _ansiColors = DarkStandardColors;
                _selectionBg = Color.FromArgb(255, 68, 71, 90);
            }
            else if (settings.ColorScheme == "Solarized Dark")
            {
                _defaultBg = Color.FromArgb(255, 0, 43, 54);
                _defaultFg = Color.FromArgb(255, 131, 148, 150);
                _ansiColors = DarkStandardColors;
                _selectionBg = Color.FromArgb(255, 7, 54, 66);
            }
            else if (settings.ColorScheme == "Catppuccin Mocha")
            {
                _defaultBg = Color.FromArgb(255, 30, 30, 46);
                _defaultFg = Color.FromArgb(255, 205, 214, 244);
                _ansiColors = CatppuccinMochaColors;
                _selectionBg = Color.FromArgb(255, 88, 91, 112);
            }
            else if (settings.ColorScheme == "Tokyo Night")
            {
                _defaultBg = Color.FromArgb(255, 26, 27, 38);
                _defaultFg = Color.FromArgb(255, 192, 202, 245);
                _ansiColors = TokyoNightColors;
                _selectionBg = Color.FromArgb(255, 65, 72, 104);
            }
            else if (settings.ColorScheme == "Nord")
            {
                _defaultBg = Color.FromArgb(255, 46, 52, 64);
                _defaultFg = Color.FromArgb(255, 216, 222, 233);
                _ansiColors = NordColors;
                _selectionBg = Color.FromArgb(255, 67, 76, 94);
            }
            else if (settings.ColorScheme == "Gruvbox Dark")
            {
                _defaultBg = Color.FromArgb(255, 40, 40, 40);
                _defaultFg = Color.FromArgb(255, 235, 219, 178);
                _ansiColors = GruvboxDarkColors;
                _selectionBg = Color.FromArgb(255, 80, 73, 69);
            }
            else // One Dark / Default
            {
                _defaultBg = Color.FromArgb(255, 12, 12, 12);
                _defaultFg = Color.FromArgb(255, 204, 204, 204);
                _ansiColors = DarkStandardColors;
                _selectionBg = Color.FromArgb(255, 38, 79, 120);
            }
            // _defaultBg stays fully opaque — used only for cursor inversion & color math.
            // The canvas itself is kept transparent so the window's Mica/Acrylic backdrop
            // shows through terminal empty cells, making the terminal blend with the UI.
            if (Canvas != null && Canvas.ReadyToDraw)
            {
                Canvas.ClearColor = Microsoft.UI.Colors.Transparent;
                RootGrid.Background = null; // transparent — let Mica show through
                UpdateFont();
            }
            
            if (_session != null && _session.Buffer != null && settings != null)
            {
                _session.Buffer.MaxScrollback = settings.ScrollbackLines;
            }
            
            RequestRedraw();
        }

        private void RequestRedraw()
        {
            if (!_isLoaded) return;
            
            _needsRedraw = true;
            
            // Debounce rendering to ~60FPS
            if (!_isDrawing)
            {
                _isDrawing = true;
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await Task.Delay(16); // ~1 frame at 60hz
                    if (_needsRedraw && Canvas != null && Canvas.ReadyToDraw)
                    {
                        _needsRedraw = false;
                        Canvas.Invalidate();
                    }
                    _isDrawing = false;
                });
            }
        }

        // ── Win2D Lifecycle & Measuring ───────────────────────────────────────

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            this.Focus(FocusState.Programmatic);
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            if (_session != null)
            {
                _session.Buffer.BufferChanged -= RequestRedraw;
            }
            Canvas.RemoveFromVisualTree();
            Canvas = null!;
        }

        private void Canvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            UpdateFont();
            // Don't call UpdatePtySize here – sender.Size may be (0,0) before layout.
            // The real connection is deferred to the first SizeChanged with a valid size.
        }

        private void UpdateFont()
        {
            if (Canvas == null || !Canvas.ReadyToDraw) return;

            _textFormat = new CanvasTextFormat
            {
                FontFamily = string.IsNullOrEmpty(_settings?.FontFamily) ? "Consolas" : _settings.FontFamily,
                FontSize = (float)(_settings?.FontSize ?? 16),
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Top
            };

            // Keep canvas transparent so the Mica/Acrylic backdrop shows through.
            // _defaultBg is used only as a reference for cursor/text rendering, not as fill.
            Canvas.ClearColor = Microsoft.UI.Colors.Transparent;
            RootGrid.Background = null;

            // Use a 20-char string to average out any left/right layout padding.
            // This gives the most accurate per-character advance width for col calculation.
            const int measureCount = 20;
            string measureStr = new string('M', measureCount);
            using var longLayout = new CanvasTextLayout(Canvas, measureStr, _textFormat, 0.0f, 0.0f);
            _charWidth = longLayout.LayoutBounds.Width / measureCount;
            if (_charWidth <= 0) _charWidth = 8;

            // Use LineSpacing from the layout (includes ascender + descender + gap).
            // CanvasTextLayout.LineSpacing is the actual rendered line height.
            _charHeight = longLayout.LineSpacing;
            if (_charHeight <= 0) _charHeight = longLayout.LayoutBounds.Height;
            if (_charHeight <= 0) _charHeight = 16;
            _charHeight = Math.Ceiling(_charHeight);

            if (Canvas.ActualWidth > 0 && Canvas.ActualHeight > 0)
            {
                UpdatePtySize(Canvas.ActualWidth, Canvas.ActualHeight);
            }
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = e.NewSize.Width;
            double h = e.NewSize.Height;

            // Ignore trivially small sizes before layout is complete
            if (w < 50 || h < 30 || _charWidth <= 0 || _charHeight <= 0) return;

            UpdatePtySize(w, h);

            // First meaningful size event → now connect with the correct cols
            if (!_hasConnected && _session != null &&
                _session.State == TerminalSession.SessionState.Disconnected)
            {
                _hasConnected = true;
                _ = _session.ConnectAsync();
            }
        }

        private void UpdatePtySize(double width, double height)
        {
            if (_session == null || _charWidth <= 0 || _charHeight <= 0) return;

            int cols = Math.Max(10, (int)(width / _charWidth));
            int rows = Math.Max(3,  (int)(height / _charHeight));

            bool wasConnected = _session.State == TerminalSession.SessionState.Connected;
            bool sizeChanged  = cols != _session.Buffer.Cols || rows != _session.Buffer.Rows;

            _session.Buffer.Resize(cols, rows);
            _session.PtyBridge.SetSize(cols, rows, _session.Transport);
            RequestRedraw();

        }

        // ── Rendering Loop ────────────────────────────────────────────────────

        private bool IsCellSelected(int x, int y)
        {
            if (_selectionStart == null || _selectionEnd == null) return false;

            var a = _selectionStart.Value;
            var b = _selectionEnd.Value;

            // Normalise so 'start' is always the earlier position
            (int x, int y) start, end;
            if (a.y < b.y || (a.y == b.y && a.x <= b.x))
            { start = a; end = b; }
            else
            { start = b; end = a; }

            // Single-cell selection = nothing highlighted (pure click, no drag)
            if (start.x == end.x && start.y == end.y) return false;

            if (start.y == end.y)
                return y == start.y && x >= start.x && x <= end.x;

            if (y == start.y) return x >= start.x;
            if (y == end.y)   return x <= end.x;
            return y > start.y && y < end.y;
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_session == null || _textFormat == null) return;

            var ds = args.DrawingSession;
            var buffer = _session.Buffer;
            
            int rows = buffer.Rows;
            int cols = buffer.Cols;
            
            int totalLines = buffer.Scrollback.Count + buffer.Lines.Count;
            int startLineIndex = Math.Max(0, totalLines - rows - _scrollOffset);

            StringBuilder textChunk = new StringBuilder(cols);

            for (int y = 0; y < rows; y++)
            {
                int absY = startLineIndex + y;
                TerminalRow? row = null;

                if (absY < buffer.Scrollback.Count)
                {
                    row = buffer.Scrollback[absY];
                }
                else if (absY - buffer.Scrollback.Count < buffer.Lines.Count)
                {
                    row = buffer.Lines[absY - buffer.Scrollback.Count];
                }

                if (row == null || row.Cells == null) continue;

                int startX = 0;
                TerminalCell currentAttr = row.Cells[0];
                textChunk.Clear();

                for (int x = 0; x < Math.Min(cols, row.Cells.Length); x++)
                {
                    var cell = row.Cells[x];

                    if (IsCellSelected(x, y + _scrollOffset))
                    {
                        cell.BgColor = TerminalCell.SelectionBgMask;
                    }

                    // Draw cursor background block (only if we are not scrolled up)
                    if (x == buffer.CursorX && y == (buffer.CursorY + buffer.Rows - Math.Min(rows, buffer.Lines.Count) + _scrollOffset) && _scrollOffset == 0 && this.FocusState != FocusState.Unfocused)
                    {
                        if (textChunk.Length > 0)
                        {
                            DrawChunk(ds, textChunk.ToString(), startX, y, currentAttr);
                            textChunk.Clear();
                        }
                        
                        var invertedAttr = cell;
                        invertedAttr.FgColor = (uint)((_defaultBg.A << 24) | (_defaultBg.R << 16) | (_defaultBg.G << 8) | _defaultBg.B);
                        
                        if (_settings.CursorStyle == "Underline")
                        {
                            ds.DrawLine((float)(x * _charWidth), (float)((y + 1) * _charHeight - 1),
                                        (float)((x + 1) * _charWidth), (float)((y + 1) * _charHeight - 1), _defaultFg, 2);
                            DrawChunk(ds, cell.Char.ToString(), x, y, cell);
                        }
                        else if (_settings.CursorStyle == "Bar")
                        {
                            ds.DrawLine((float)(x * _charWidth + 1), (float)(y * _charHeight),
                                        (float)(x * _charWidth + 1), (float)((y + 1) * _charHeight), _defaultFg, 2);
                            DrawChunk(ds, cell.Char.ToString(), x, y, cell);
                        }
                        else
                        {
                            ds.FillRectangle((float)(x * _charWidth), (float)(y * _charHeight),
                                             (float)_charWidth, (float)_charHeight, _defaultFg);
                            DrawChunk(ds, cell.Char.ToString(), x, y, invertedAttr);
                        }
                        
                        startX = x + 1;
                        if (x + 1 < row.Cells.Length) currentAttr = row.Cells[x + 1];
                        continue;
                    }

                    if (cell.FgColor != currentAttr.FgColor || 
                        cell.BgColor != currentAttr.BgColor ||
                        cell.IsBold != currentAttr.IsBold)
                    {
                        if (textChunk.Length > 0)
                        {
                            DrawChunk(ds, textChunk.ToString(), startX, y, currentAttr);
                            textChunk.Clear();
                        }
                        startX = x;
                        currentAttr = cell;
                    }

                    textChunk.Append(cell.Char == 0 ? ' ' : cell.Char);
                }

                if (textChunk.Length > 0)
                {
                    DrawChunk(ds, textChunk.ToString(), startX, y, currentAttr);
                }
            }
        }

        private void DrawChunk(CanvasDrawingSession ds, string text, int startX, int y, TerminalCell attr)
        {
            if (string.IsNullOrWhiteSpace(text) && attr.BgColor == TerminalCell.DefaultBg) return;

            float xPos = (float)(startX * _charWidth);
            float yPos = (float)(y * _charHeight);

            if (attr.BgColor != TerminalCell.DefaultBg)
            {
                Color bg = ParseColor(attr.BgColor, _defaultBg);
                ds.FillRectangle(xPos, yPos, (float)(text.Length * _charWidth), (float)_charHeight, bg);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                Color fg = ParseColor(attr.FgColor, _defaultFg);
                
                // Temp bold implementation: use standard text format but draw twice slightly offset
                // Real bold needs font weight changes, but caching formats is complex for Phase 4
                ds.DrawText(text, xPos, yPos, fg, _textFormat);
                if (attr.IsBold)
                {
                    ds.DrawText(text, xPos + 0.5f, yPos, fg, _textFormat);
                }
            }
        }

        private Color ParseColor(uint c, Color fallback)
        {
            if (c == TerminalCell.SelectionBgMask) return _selectionBg;
            if (c == TerminalCell.DefaultFg || c == TerminalCell.DefaultBg) return fallback;
            if ((c & 0xFF000000) == TerminalCell.IndexedColorMask)
            {
                int idx = (int)(c & 0xFF);
                if (idx >= 0 && idx < 16) return _ansiColors[idx];
                // Simplify 256 colors: fallback to grey for index 16-255 if not matched above
                return Color.FromArgb(255, 136, 136, 136); 
            }
            return Color.FromArgb(255, (byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));
        }

        // ── Input Handling ────────────────────────────────────────────────────

        private void UserControl_GettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            RequestRedraw(); // Show cursor
        }


        private void UserControl_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            this.Focus(FocusState.Pointer);
            e.Handled = true;

            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed)
            {
                // Right click: paste clipboard into SSH stream
                _ = PasteFromClipboard();
                // Clear any active selection
                _isSelecting   = false;
                _selectionStart = null;
                _selectionEnd   = null;
                RequestRedraw();
            }
            else if (point.Properties.IsLeftButtonPressed)
            {
                // Left click: clear old selection, begin new one
                _isSelecting   = true;
                this.CapturePointer(e.Pointer);
                int x = Math.Max(0, (int)(point.Position.X / _charWidth));
                int y = Math.Max(0, (int)(point.Position.Y / _charHeight)) + _scrollOffset;
                _selectionStart = (x, y);
                _selectionEnd   = (x, y); // same = no visible highlight until drag
                // Don't call RequestRedraw here – avoids the flicker on plain click.
            }
        }

        private void UserControl_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isSelecting && _selectionStart != null)
            {
                var point = e.GetCurrentPoint(this);
                int x = Math.Max(0, (int)(point.Position.X / _charWidth));
                int y = Math.Max(0, (int)(point.Position.Y / _charHeight)) + _scrollOffset;
                _selectionEnd = (x, y);
                RequestRedraw();
            }
        }

        private void UserControl_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            this.ReleasePointerCapture(e.Pointer);
            if (_isSelecting)
            {
                _isSelecting = false;
                if (_selectionStart != null && _selectionEnd != null && _selectionStart != _selectionEnd)
                {
                    // Copy text to clipboard
                    CopyToClipboard();
                }
                else
                {
                    _selectionStart = null;
                    _selectionEnd = null;
                    RequestRedraw();
                }
            }
        }

        private void UserControl_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_session == null || _session.Buffer == null) return;

            var point = e.GetCurrentPoint(this);
            int delta = point.Properties.MouseWheelDelta;
            int scrollLines = -(delta / 40); // 120 per notch is typical -> 3 lines per notch

            int newOffset = _scrollOffset + scrollLines;
            if (newOffset < 0) newOffset = 0;
            if (newOffset > _session.Buffer.Scrollback.Count) newOffset = _session.Buffer.Scrollback.Count;

            if (newOffset != _scrollOffset)
            {
                _scrollOffset = newOffset;
                RequestRedraw();
            }
        }

        private async System.Threading.Tasks.Task PasteFromClipboard()
        {
            if (_session == null || !_session.Transport.IsConnected) return;

            var dataPackageView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                string text = await dataPackageView.GetTextAsync();
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                _session.Transport.SendRaw(bytes);
            }
        }

        private void CopyToClipboard()
        {
            if (_selectionStart == null || _selectionEnd == null || _session == null) return;

            var start = _selectionStart.Value;
            var end = _selectionEnd.Value;

            if (start.y > end.y || (start.y == end.y && start.x > end.x))
            {
                var temp = start;
                start = end;
                end = temp;
            }

            string text = _session.Buffer.GetText(start.x, start.y, end.x, end.y);
            if (!string.IsNullOrEmpty(text))
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            }
        }

        // WinUI 3 keyboard routing: KeyDown for control keys, CharacterReceived for text
        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (_session == null || !_session.Transport.IsConnected) return;

            byte[]? seq = null;
            bool handled = true;

            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            switch (e.Key)
            {
                case Windows.System.VirtualKey.Up:    seq = new byte[] { 27, (byte)'[', (byte)'A' }; break;
                case Windows.System.VirtualKey.Down:  seq = new byte[] { 27, (byte)'[', (byte)'B' }; break;
                case Windows.System.VirtualKey.Right: seq = new byte[] { 27, (byte)'[', (byte)'C' }; break;
                case Windows.System.VirtualKey.Left:  seq = new byte[] { 27, (byte)'[', (byte)'D' }; break;
                
                case Windows.System.VirtualKey.Insert: seq = new byte[] { 27, (byte)'[', (byte)'2', (byte)'~' }; break;
                case Windows.System.VirtualKey.Delete: seq = new byte[] { 27, (byte)'[', (byte)'3', (byte)'~' }; break;
                case Windows.System.VirtualKey.Home:   seq = new byte[] { 27, (byte)'[', (byte)'H' }; break;
                case Windows.System.VirtualKey.End:    seq = new byte[] { 27, (byte)'[', (byte)'F' }; break;
                case Windows.System.VirtualKey.PageUp: seq = new byte[] { 27, (byte)'[', (byte)'5', (byte)'~' }; break;
                case Windows.System.VirtualKey.PageDown:seq= new byte[] { 27, (byte)'[', (byte)'6', (byte)'~' }; break;
                
                case Windows.System.VirtualKey.Tab:
                    seq = new byte[] { 9 };
                    break;
                case Windows.System.VirtualKey.Enter:
                    seq = new byte[] { 13 }; // CR
                    break;
                case Windows.System.VirtualKey.Back:
                    seq = new byte[] { 127 }; // DEL (Backspace in most modern Linux)
                    break;
                case Windows.System.VirtualKey.Escape:
                    seq = new byte[] { 27 };
                    break;

                case Windows.System.VirtualKey.C:
                    if (ctrl) seq = new byte[] { 3 }; // Ctrl+C
                    else handled = false;
                    break;
                case Windows.System.VirtualKey.D:
                    if (ctrl) seq = new byte[] { 4 }; // Ctrl+D
                    else handled = false;
                    break;
                case Windows.System.VirtualKey.L:
                    if (ctrl) seq = new byte[] { 12 }; // Ctrl+L (Clear screen)
                    else handled = false;
                    break;
                case Windows.System.VirtualKey.Z:
                    if (ctrl) seq = new byte[] { 26 }; // Ctrl+Z
                    else handled = false;
                    break;

                default:
                    handled = false;
                    break;
            }

            if (seq != null)
            {
                _session.Transport.SendRaw(seq);
                e.Handled = true;
            }
            else if (handled)
            {
                e.Handled = true;
            }
            
            base.OnKeyDown(e);
        }

        // Add CharacterReceived via constructor or event binding since UserControl doesn't have a virtual method for it
        private void UIElement_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
        {
            if (_session == null || !_session.Transport.IsConnected) return;

            // Ignore ASCII control characters that are handled by OnKeyDown (e.g. Enter, Backspace)
            if (args.Character < 32 && args.Character != 9 && args.Character != 10 && args.Character != 13) return;

            // Handle printable characters
            byte[] bytes = Encoding.UTF8.GetBytes(new char[] { args.Character });
            _session.Transport.SendRaw(bytes);
            args.Handled = true;
        }

        // Removed OnApplyTemplate since we register in constructor now
    }
}
