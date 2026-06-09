using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using SwellSSH.Models;
using SwellSSH.Services;

namespace SwellSSH.Terminal
{
    /// <summary>
    /// Wraps SSH.NET's SshClient + ShellStream lifecycle.
    /// - Connects with password or private key (auto-detected)
    /// - Opens an interactive shell with terminal type "xterm-256color"
    /// - Fires DataReceived for every chunk of output from the server
    /// - Supports resize via ResizeTerminal()
    /// - Reconnects automatically on disconnect (exponential back-off, max 3 retries)
    /// </summary>
    public sealed class SshTransport : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired on the thread-pool whenever the server sends output bytes.</summary>
        public event Action<byte[]>? DataReceived;

        /// <summary>Fired when the connection drops unexpectedly.</summary>
        public event Action<Exception?>? Disconnected;

        // ── State ─────────────────────────────────────────────────────────────

        public bool IsConnected => _client?.IsConnected == true && _shell != null;
        public ConnectionProfile? Profile { get; private set; }

        private SshClient? _client;
        private ShellStream? _shell;
        private CancellationTokenSource? _readCts;
        private int _cols = 80;
        private int _rows = 24;

        // ── Connect ───────────────────────────────────────────────────────────

        /// <summary>
        /// Establishes SSH connection and opens an interactive shell.
        /// Throws SshAuthenticationException / SocketException on failure — caller should show InfoBar.
        /// </summary>
        public async Task ConnectAsync(ConnectionProfile profile, int cols = 120, int rows = 30)
        {
            Profile = profile;
            _cols = cols;
            _rows = rows;

            await Task.Run(() =>
            {
                _client = BuildClient(profile);
                _client.Connect();
                _client.ErrorOccurred += OnClientError;

                _shell = _client.CreateShellStream(
                    terminalName: "xterm-256color",
                    columns: (uint)cols,
                    rows: (uint)rows,
                    width: 0,
                    height: 0,
                    bufferSize: 4096);
            });

            // Start background read loop
            _readCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(_readCts.Token));
        }

        // ── Input ─────────────────────────────────────────────────────────────

        public void SendInput(string text)
        {
            if (_shell == null || !IsConnected) return;
            try
            {
                _shell.Write(text);
                _shell.Flush();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SSH] SendInput error: {ex.Message}");
            }
        }

        public void SendRaw(byte[] data)
        {
            if (_shell == null || !IsConnected) return;
            try
            {
                _shell.Write(data, 0, data.Length);
                _shell.Flush();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SSH] SendRaw error: {ex.Message}");
            }
        }

        // ── Resize ────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a PTY window-size change request to the remote server.
        /// Call this whenever the TerminalView is resized.
        /// </summary>
        public void ResizeTerminal(int cols, int rows)
        {
            if (_shell == null || !IsConnected || (cols == _cols && rows == _rows)) return;
            _cols = cols;
            _rows = rows;
            try
            {
                // ShellStream doesn't expose SendWindowChangeRequest publicly, but its internal channel does.
                // Search by interface name to be robust against SSH.NET version upgrades renaming the field.
                var field = typeof(ShellStream).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(f => f.FieldType.Name.Contains("IChannelSession") || f.Name.Contains("channel"));
                    
                if (field?.GetValue(_shell) is object channel)
                {
                    var method = channel.GetType().GetMethod("SendWindowChangeRequest",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    method?.Invoke(channel, new object[] { (uint)cols, (uint)rows, (uint)0, (uint)0 });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SSH] Resize error: {ex.Message}");
            }
        }

        // ── Background Read Loop ──────────────────────────────────────────────

        /// <summary>
        /// Reads raw bytes from ShellStream continuously and fires DataReceived.
        /// IMPORTANT: must run continuously to prevent SSH pipe buffer from filling up.
        /// </summary>
        private async Task ReadLoopAsync(CancellationToken token)
        {
            var buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_shell == null) break;

                    int bytesRead = 0;
                    // ShellStream.Read blocks until data is available (or stream closes)
                    await Task.Run(() =>
                    {
                        try { bytesRead = _shell.Read(buffer, 0, buffer.Length); }
                        catch { bytesRead = -1; }
                    }, token);

                    if (bytesRead < 0) break;   // stream closed
                    if (bytesRead == 0)          // no data yet — small yield
                    {
                        await Task.Delay(10, token);
                        continue;
                    }

                    var chunk = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                    DataReceived?.Invoke(chunk);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SSH] ReadLoop exception: {ex.Message}");
                    break;
                }
            }

            // If we exit the loop unexpectedly, notify caller
            if (!token.IsCancellationRequested)
                Disconnected?.Invoke(null);
        }

        // ── Reconnect (exponential back-off) ──────────────────────────────────

        private async void OnClientError(object? sender, ExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[SSH] ClientError: {e.Exception.Message}");
            await ReconnectWithBackoffAsync();
        }

        private async Task ReconnectWithBackoffAsync()
        {
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                int delaySec = (int)Math.Pow(2, attempt); // 2, 4, 8 seconds
                System.Diagnostics.Debug.WriteLine($"[SSH] Reconnect attempt {attempt}/{maxRetries} in {delaySec}s...");
                
                string msg = $"\r\n\x1b[33m[SSH] Connection lost. Reconnecting attempt {attempt}/{maxRetries} in {delaySec}s...\x1b[0m\r\n";
                DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(msg));
                
                await Task.Delay(TimeSpan.FromSeconds(delaySec));
                try
                {
                    if (Profile == null) return;
                    DisposeShell();
                    _client?.Connect();
                    _shell = _client!.CreateShellStream("xterm-256color",
                        (uint)_cols, (uint)_rows, 0, 0, 4096);
                    _readCts = new CancellationTokenSource();
                    _ = Task.Run(() => ReadLoopAsync(_readCts.Token));
                    
                    string successMsg = $"\r\n\x1b[32m[SSH] Reconnected successfully.\x1b[0m\r\n";
                    DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(successMsg));
                    
                    System.Diagnostics.Debug.WriteLine($"[SSH] Reconnected successfully.");
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SSH] Reconnect attempt {attempt} failed: {ex.Message}");
                }
            }

            // Give up after max retries
            string failMsg = $"\r\n\x1b[31m[SSH] Reconnect failed after 3 attempts. Disconnected.\x1b[0m\r\n";
            DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(failMsg));
            
            Disconnected?.Invoke(new Exception("Reconnect failed after 3 attempts."));
        }

        // ── Auth Builder ──────────────────────────────────────────────────────

        private static SshClient BuildClient(ConnectionProfile profile)
        {
            string host = profile.Host;
            int port = profile.Port;
            string user = profile.Username;

            SshClient client;
            if (profile.AuthType == "PrivateKey")
            {
                string keyPath = profile.PrivateKeyPath;
                string passphrase = ConnectionStorage.DecryptSecret(profile.EncryptedPassphrase);

                PrivateKeyFile keyFile = string.IsNullOrEmpty(passphrase)
                    ? new PrivateKeyFile(keyPath)
                    : new PrivateKeyFile(keyPath, passphrase);

                client = new SshClient(host, port, user, keyFile);
            }
            else
            {
                string password = ConnectionStorage.DecryptSecret(profile.EncryptedPassword);
                client = new SshClient(host, port, user, password);
            }

            // 应用 Keepalive 心跳（防止 NAT/防火墙空闲断连）
            if (profile.KeepAliveIntervalSeconds > 0)
            {
                client.KeepAliveInterval = TimeSpan.FromSeconds(profile.KeepAliveIntervalSeconds);
            }

            return client;
        }

        // ── Disconnect / Dispose ──────────────────────────────────────────────

        public void Disconnect()
        {
            _readCts?.Cancel();
            DisposeShell();
            try { _client?.Disconnect(); } catch { }
        }

        private void DisposeShell()
        {
            try { _shell?.Dispose(); } catch { }
            _shell = null;
        }

        public void Dispose()
        {
            Disconnect();
            _readCts?.Dispose();
            _client?.Dispose();
        }
    }
}
