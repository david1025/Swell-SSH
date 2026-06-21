namespace SwellSSH.Terminal
{
    /// <summary>
    /// Interface implemented by TerminalBuffer to handle parsed VT actions.
    /// The VtParser drives this interface.
    /// </summary>
    public interface ITerminalActionHandler
    {
        /// <summary>Print printable characters at the current cursor position.</summary>
        void Print(System.ReadOnlySpan<char> text);

        /// <summary>Execute a control character (e.g. \n, \r, \b, \t, BEL).</summary>
        void ExecuteControlCharacter(byte b);

        /// <summary>Handle a CSI (Control Sequence Introducer) command (e.g. CSI 1;32m).</summary>
        void CsiDispatch(char action, System.ReadOnlySpan<int> parameters, bool hasQuestionMark);

        /// <summary>Handle an OSC (Operating System Command) (e.g. OSC 0;Window Title ST).</summary>
        void OscDispatch(int command, string payload);

        /// <summary>Handle an Escape (ESC) sequence that isn't CSI/OSC (e.g. ESC M for reverse index).</summary>
        void EscDispatch(char action);
    }
}
