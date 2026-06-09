using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SwellSSH.Terminal
{
    /// <summary>
    /// Wraps the Windows Pseudo Console (ConPTY) API for PTY resize signaling.
    ///
    /// In SwellSSH, ConPTY's primary role is NOT to host a local process.
    /// It is used so that when the TerminalView is resized, we can send a proper
    /// SIGWINCH / window-change request via SshTransport.ResizeTerminal().
    ///
    /// The actual terminal resize is handled by SshTransport.ResizeTerminal(),
    /// which calls ShellStream.SendWindowChangeRequest(). This class exists as
    /// a thin bridge that also tracks current dimensions and handles edge cases.
    ///
    /// NOTE: If a full local ConPTY pipe is needed in the future (e.g., for a local
    /// shell tab), this class can be extended to call CreatePseudoConsole directly.
    /// </summary>
    public sealed class ConPtyBridge : IDisposable
    {
        private int _cols;
        private int _rows;
        private bool _disposed;

        public int Cols => _cols;
        public int Rows => _rows;

        public ConPtyBridge(int initialCols = 120, int initialRows = 30)
        {
            _cols = Math.Max(1, initialCols);
            _rows = Math.Max(1, initialRows);
        }

        /// <summary>
        /// Called when the TerminalView control is resized.
        /// Calculates the new terminal grid dimensions from pixel size and char size,
        /// then delegates the actual PTY resize to the provided SshTransport.
        /// </summary>
        /// <param name="pixelWidth">New width of the TerminalView in pixels.</param>
        /// <param name="pixelHeight">New height of the TerminalView in pixels.</param>
        /// <param name="charWidth">Width of one character cell in pixels.</param>
        /// <param name="charHeight">Height of one character cell in pixels.</param>
        /// <param name="transport">The active SSH transport to notify.</param>
        public void OnViewResized(
            double pixelWidth, double pixelHeight,
            double charWidth, double charHeight,
            SshTransport? transport)
        {
            if (charWidth <= 0 || charHeight <= 0) return;

            int newCols = Math.Max(10, (int)(pixelWidth  / charWidth));
            int newRows = Math.Max(3,  (int)(pixelHeight / charHeight));

            if (newCols == _cols && newRows == _rows) return;

            _cols = newCols;
            _rows = newRows;

            System.Diagnostics.Debug.WriteLine($"[PTY] Resize → {newCols}×{newRows}");
            transport?.ResizeTerminal(newCols, newRows);
        }

        /// <summary>
        /// Directly sets a new terminal size and notifies the transport.
        /// Use when cols/rows are already known (e.g., from TerminalBuffer).
        /// </summary>
        public void SetSize(int cols, int rows, SshTransport? transport)
        {
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            if (cols == _cols && rows == _rows) return;
            _cols = cols;
            _rows = rows;
            transport?.ResizeTerminal(cols, rows);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // No native ConPTY handle to close in the current design.
            // If CreatePseudoConsole is used in the future, close it here.
        }

        // ── Native API stubs (for future full ConPTY integration) ─────────────
        // These are declared but not called yet; CsWin32 will generate them from
        // NativeMethods.txt. Kept here as documentation of intended future use.

        // CreatePseudoConsole  → create a PTY for local shell tabs
        // ResizePseudoConsole  → would mirror SshTransport.ResizeTerminal for local shells
        // ClosePseudoConsole   → would be called in Dispose
    }
}
