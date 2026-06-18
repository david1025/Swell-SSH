using System;
using System.Text;

namespace SwellSSH.Terminal
{
    /// <summary>Streaming DEC/ANSI parser with batched UTF-8 decoding.</summary>
    public sealed class VtParser
    {
        private readonly ITerminalActionHandler _handler;
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();

        private enum State { Ground, Escape, CsiEntry, CsiParam, OscString, OscEscape }
        private State _state;

        private readonly int[] _csiParams = new int[16];
        private int _csiParamCount;
        private int _currentParam;
        private bool _hasParam;
        private bool _hasQuestionMark;
        private readonly StringBuilder _oscPayload = new();

        public VtParser(ITerminalActionHandler handler) => _handler = handler;

        public void Feed(ReadOnlySpan<byte> data)
        {
            int i = 0;
            while (i < data.Length)
            {
                byte b = data[i];

                if (_state == State.Ground && b >= 0x20 && b != 0x7F)
                {
                    int start = i++;
                    while (i < data.Length && data[i] >= 0x20 && data[i] != 0x7F) i++;
                    DecodePrintable(data.Slice(start, i - start), appendToOsc: false);
                    continue;
                }

                if (_state == State.OscString && b >= 0x20 && b != 0x7F)
                {
                    int start = i++;
                    while (i < data.Length && data[i] >= 0x20 && data[i] != 0x7F) i++;
                    DecodePrintable(data.Slice(start, i - start), appendToOsc: true);
                    continue;
                }

                i++;
                if (_state == State.OscString)
                {
                    if (b == 0x07) EndOsc();
                    else if (b == 0x1B) _state = State.OscEscape;
                    continue;
                }

                if (_state == State.OscEscape)
                {
                    if (b == (byte)'\\') EndOsc();
                    else
                    {
                        _state = State.OscString;
                        if (b >= 0x20 && b != 0x7F)
                            DecodePrintable(data.Slice(i - 1, 1), appendToOsc: true);
                    }
                    continue;
                }

                if (b == 0x1B)
                {
                    EnterEscape();
                    continue;
                }

                if (b <= 0x1F || b == 0x7F)
                {
                    if (b != 0x00 && b != 0x7F) _handler.ExecuteControlCharacter(b);
                    continue;
                }

                switch (_state)
                {
                    case State.Escape:
                        if (b == (byte)'[') EnterCsi();
                        else if (b == (byte)']')
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
                        if (b == (byte)'?')
                        {
                            _hasQuestionMark = true;
                            _state = State.CsiParam;
                        }
                        else if (b >= (byte)'0' && b <= (byte)'9')
                        {
                            _state = State.CsiParam;
                            _currentParam = b - (byte)'0';
                            _hasParam = true;
                        }
                        else if (b == (byte)';')
                        {
                            _state = State.CsiParam;
                            AddCsiParam(0);
                        }
                        else if (b >= 0x40 && b <= 0x7E)
                        {
                            DispatchCsi((char)b);
                        }
                        break;

                    case State.CsiParam:
                        if (b >= (byte)'0' && b <= (byte)'9')
                        {
                            _currentParam = Math.Min(999999, _currentParam * 10 + b - (byte)'0');
                            _hasParam = true;
                        }
                        else if (b == (byte)';')
                        {
                            AddCsiParam(_hasParam ? _currentParam : 0);
                            _currentParam = 0;
                            _hasParam = false;
                        }
                        else if (b >= 0x40 && b <= 0x7E)
                        {
                            if (_hasParam) AddCsiParam(_currentParam);
                            DispatchCsi((char)b);
                        }
                        break;
                }
            }
        }

        private void DecodePrintable(ReadOnlySpan<byte> bytes, bool appendToOsc)
        {
            Span<char> chars = stackalloc char[512];
            while (!bytes.IsEmpty)
            {
                int take = Math.Min(bytes.Length, 512);
                int written = _utf8Decoder.GetChars(bytes.Slice(0, take), chars, flush: false);
                if (written > 0)
                {
                    if (appendToOsc) _oscPayload.Append(chars.Slice(0, written));
                    else _handler.Print(chars.Slice(0, written));
                }
                bytes = bytes.Slice(take);
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
            _csiParamCount = 0;
            _currentParam = 0;
            _hasParam = false;
            _hasQuestionMark = false;
        }

        private void AddCsiParam(int value)
        {
            if (_csiParamCount < _csiParams.Length) _csiParams[_csiParamCount++] = value;
        }

        private void DispatchCsi(char action)
        {
            _handler.CsiDispatch(action, _csiParams.AsSpan(0, _csiParamCount), _hasQuestionMark);
            _state = State.Ground;
        }

        private void EndOsc()
        {
            _state = State.Ground;
            ReadOnlySpan<char> osc = _oscPayload.ToString().AsSpan();
            int sep = osc.IndexOf(';');
            if (sep >= 0 && int.TryParse(osc.Slice(0, sep), out int command))
                _handler.OscDispatch(command, osc.Slice(sep + 1).ToString());
            else if (int.TryParse(osc, out command))
                _handler.OscDispatch(command, string.Empty);
            _oscPayload.Clear();
        }
    }
}
