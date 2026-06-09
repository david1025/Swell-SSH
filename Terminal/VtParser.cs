using System;
using System.Collections.Generic;
using System.Text;

namespace SwellSSH.Terminal
{
    /// <summary>
    /// Parses a stream of raw bytes into VT actions based on the DEC ANSI standard.
    /// Handles UTF-8 decoding for printable characters.
    /// </summary>
    public sealed class VtParser
    {
        private readonly ITerminalActionHandler _handler;
        private readonly Decoder _utf8Decoder;
        private readonly char[] _charBuffer = new char[1];

        private enum State
        {
            Ground,
            Escape,
            CsiEntry,
            CsiParam,
            OscString,
            OscEscape // Inside OSC, received ESC (potential ST)
        }

        private State _state = State.Ground;

        // CSI parameters collection
        private readonly List<int> _csiParams = new(8);
        private int _currentParam = 0;
        private bool _hasParam = false;
        private bool _hasQuestionMark = false; // e.g. CSI ? 25 h

        // OSC payload collection
        private readonly StringBuilder _oscPayload = new();

        public VtParser(ITerminalActionHandler handler)
        {
            _handler = handler;
            _utf8Decoder = Encoding.UTF8.GetDecoder();
        }

        public void Feed(ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];

                // C0 Controls (0x00 - 0x1F) always execute immediately anywhere
                if (b <= 0x1F)
                {
                    if (b == 0x1B) // ESC
                    {
                        EnterEscape();
                        continue;
                    }
                    else if (b == 0x07) // BEL
                    {
                        if (_state == State.OscString)
                            EndOsc();
                        else
                            _handler.ExecuteControlCharacter(b);
                        continue;
                    }
                    else if (b != 0x00 && b != 0x7F) // Ignore NUL and DEL
                    {
                        // Some terminals execute C0 even inside OSC, but we'll ignore it there to be safe
                        if (_state != State.OscString)
                            _handler.ExecuteControlCharacter(b);
                        continue;
                    }
                    continue;
                }

                switch (_state)
                {
                    case State.Ground:
                        if (b >= 0x20)
                            ProcessPrintable(b);
                        break;

                    case State.Escape:
                        if (b == '[')
                        {
                            EnterCsi();
                        }
                        else if (b == ']')
                        {
                            _state = State.OscString;
                            _oscPayload.Clear();
                        }
                        else if (b >= 0x30 && b <= 0x7E)
                        {
                            _handler.EscDispatch((char)b);
                            _state = State.Ground;
                        }
                        break;

                    case State.CsiEntry:
                        if (b == '?')
                        {
                            _hasQuestionMark = true;
                            _state = State.CsiParam;
                        }
                        else if (b >= '0' && b <= '9')
                        {
                            _state = State.CsiParam;
                            _currentParam = b - '0';
                            _hasParam = true;
                        }
                        else if (b == ';')
                        {
                            _state = State.CsiParam;
                            _csiParams.Add(0); // empty param
                        }
                        else if (b >= 0x40 && b <= 0x7E)
                        {
                            // Dispatch with no params
                            _handler.CsiDispatch((char)b, Array.Empty<int>(), _hasQuestionMark);
                            _state = State.Ground;
                        }
                        break;

                    case State.CsiParam:
                        if (b >= '0' && b <= '9')
                        {
                            _currentParam = _currentParam * 10 + (b - '0');
                            _hasParam = true;
                        }
                        else if (b == ';')
                        {
                            _csiParams.Add(_hasParam ? _currentParam : 0);
                            _currentParam = 0;
                            _hasParam = false;
                        }
                        else if (b >= 0x40 && b <= 0x7E)
                        {
                            if (_hasParam) _csiParams.Add(_currentParam);
                            _handler.CsiDispatch((char)b, _csiParams.ToArray(), _hasQuestionMark);
                            _state = State.Ground;
                        }
                        break;

                    case State.OscString:
                        if (b == 0x1B) // ESC
                        {
                            _state = State.OscEscape;
                        }
                        else if (b >= 0x20)
                        {
                            ProcessOscPrintable(b);
                        }
                        break;

                    case State.OscEscape:
                        if (b == '\\') // ST (String Terminator)
                        {
                            EndOsc();
                        }
                        else
                        {
                            // It was just an escape inside OSC, ignore and revert
                            _state = State.OscString;
                            if (b >= 0x20) ProcessOscPrintable(b);
                        }
                        break;
                }
            }
        }

        private void EnterEscape()
        {
            _state = State.Escape;
            _hasQuestionMark = false;
        }

        private void EnterCsi()
        {
            _state = State.CsiEntry;
            _csiParams.Clear();
            _currentParam = 0;
            _hasParam = false;
            _hasQuestionMark = false;
        }

        private void EndOsc()
        {
            _state = State.Ground;
            string osc = _oscPayload.ToString();
            int sep = osc.IndexOf(';');
            if (sep >= 0 && int.TryParse(osc.Substring(0, sep), out int cmd))
            {
                _handler.OscDispatch(cmd, osc.Substring(sep + 1));
            }
            else if (int.TryParse(osc, out cmd))
            {
                _handler.OscDispatch(cmd, string.Empty);
            }
        }

        private void ProcessPrintable(byte b)
        {
            unsafe
            {
                byte* pByte = &b;
                fixed (char* pChar = _charBuffer)
                {
                    int charsUsed = _utf8Decoder.GetChars(pByte, 1, pChar, 1, flush: false);
                    if (charsUsed > 0)
                    {
                        _handler.Print(new string(_charBuffer, 0, charsUsed));
                    }
                }
            }
        }

        private void ProcessOscPrintable(byte b)
        {
            unsafe
            {
                byte* pByte = &b;
                fixed (char* pChar = _charBuffer)
                {
                    int charsUsed = _utf8Decoder.GetChars(pByte, 1, pChar, 1, flush: false);
                    if (charsUsed > 0)
                    {
                        _oscPayload.Append(_charBuffer, 0, charsUsed);
                    }
                }
            }
        }
    }
}
