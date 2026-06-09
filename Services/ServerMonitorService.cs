using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using SwellSSH.Models;

namespace SwellSSH.Services
{
    /// <summary>
    /// Background SSH monitoring service.
    /// Maintains one lightweight SSH connection per enabled server profile,
    /// polls /proc/stat, free, and df every N seconds, and fires StatsUpdated.
    /// </summary>
    public sealed class ServerMonitorService : IDisposable
    {
        // ── Singleton ────────────────────────────────────────────────────────
        public static readonly ServerMonitorService Instance = new();
        private ServerMonitorService() { }

        // ── Events ───────────────────────────────────────────────────────────
        /// <summary>Fired on a thread-pool thread after every successful (or failed) poll.</summary>
        public event Action<ServerStats>? StatsUpdated;

        // ── State ────────────────────────────────────────────────────────────
        private readonly Dictionary<string, MonitoredServer> _servers = new();
        private readonly object _lock = new();

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Start or keep monitoring any profile with EnableMonitoring=true,
        /// stop any that are no longer in the list or have monitoring disabled.</summary>
        public void Sync(IEnumerable<ConnectionProfile> profiles)
        {
            var wanted = new HashSet<string>();
            foreach (var p in profiles)
            {
                if (!p.EnableMonitoring) continue;
                wanted.Add(p.Id);
                lock (_lock)
                {
                    if (!_servers.ContainsKey(p.Id))
                    {
                        var ms = new MonitoredServer(p, this);
                        _servers[p.Id] = ms;
                        ms.Start();
                    }
                }
            }
            // Stop any that are no longer needed
            lock (_lock)
            {
                var toStop = new List<string>();
                foreach (var id in _servers.Keys)
                    if (!wanted.Contains(id)) toStop.Add(id);
                foreach (var id in toStop) StopById(id);
            }
        }

        public void Stop(string profileId)
        {
            lock (_lock) { StopById(profileId); }
        }

        private void StopById(string id)
        {
            if (_servers.TryGetValue(id, out var ms))
            {
                ms.Stop();
                _servers.Remove(id);
            }
        }

        internal void Raise(ServerStats s) => StatsUpdated?.Invoke(s);

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var ms in _servers.Values) ms.Stop();
                _servers.Clear();
            }
        }

        // ── Inner class ──────────────────────────────────────────────────────

        private sealed class MonitoredServer
        {
            private readonly ConnectionProfile _profile;
            private readonly ServerMonitorService _parent;
            private CancellationTokenSource? _cts;
            private SshClient? _client;
            private long _prevIdle, _prevTotal;   // for CPU delta

            public MonitoredServer(ConnectionProfile p, ServerMonitorService parent)
            {
                _profile = p;
                _parent  = parent;
            }

            public void Start()
            {
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => LoopAsync(_cts.Token));
            }

            public void Stop()
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                DisposeClient();
            }

            private async Task LoopAsync(CancellationToken token)
            {
                int interval = Math.Max(3, _profile.MonitorIntervalSeconds);
                // Initial connect delay – stagger multiple servers a little
                await Task.Delay(TimeSpan.FromSeconds(1), token).ContinueWith(_ => { });

                while (!token.IsCancellationRequested)
                {
                    var stats = new ServerStats
                    {
                        ConnectionId = _profile.Id,
                        UpdatedAt    = DateTime.Now
                    };

                    try
                    {
                        // (Re)connect if needed
                        if (_client == null || !_client.IsConnected)
                        {
                            _prevIdle = _prevTotal = 0;  // reset CPU delta on reconnect
                            await Task.Run(() =>
                            {
                                DisposeClient();
                                _client = Build();
                                _client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
                                _client.Connect();
                            }, token);
                        }

                        await PollAsync(stats, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        stats.HasError      = true;
                        stats.ErrorMessage  = ex.Message;
                        DisposeClient();
                    }

                    _parent.Raise(stats);

                    // Wait before next poll; use backoff on error
                    int wait = stats.HasError ? Math.Min(interval * 4, 60) : interval;
                    try { await Task.Delay(TimeSpan.FromSeconds(wait), token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            // ── SSH connection builder (mirrors SshTransport.BuildClient) ────

            private SshClient Build()
            {
                SshClient c;
                if (_profile.AuthType == "PrivateKey")
                {
                    string pp = ConnectionStorage.DecryptSecret(_profile.EncryptedPassphrase);
                    var kf = string.IsNullOrEmpty(pp)
                        ? new PrivateKeyFile(_profile.PrivateKeyPath)
                        : new PrivateKeyFile(_profile.PrivateKeyPath, pp);
                    c = new SshClient(_profile.Host, _profile.Port, _profile.Username, kf);
                }
                else
                {
                    string pwd = ConnectionStorage.DecryptSecret(_profile.EncryptedPassword);
                    c = new SshClient(_profile.Host, _profile.Port, _profile.Username, pwd);
                }
                c.KeepAliveInterval = TimeSpan.FromSeconds(30);
                return c;
            }

            private void DisposeClient()
            {
                try { _client?.Disconnect(); } catch { }
                _client?.Dispose();
                _client = null;
            }

            // ── Stats polling ────────────────────────────────────────────────

            private async Task PollAsync(ServerStats stats, CancellationToken token)
            {
                // Single compound command:
                //   line 1  → /proc/stat cpu line  (cpu fields)
                //   line 2  → free -b Mem: line
                //   line 3  → df / last line
                const string CMD =
                    "cat /proc/stat 2>/dev/null | grep '^cpu '; " +
                    "free -b 2>/dev/null | grep '^Mem:'; " +
                    "df / 2>/dev/null | tail -1";

                string output = await Task.Run(() =>
                {
                    using var cmd = _client!.CreateCommand(CMD);
                    cmd.CommandTimeout = TimeSpan.FromSeconds(6);
                    return cmd.Execute();
                }, token);

                Parse(output, stats);
            }

            private void Parse(string output, ServerStats stats)
            {
                foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim();

                    // ── /proc/stat cpu line ──────────────────────────────────
                    if (line.StartsWith("cpu "))
                    {
                        // cpu user nice system idle iowait irq softirq steal …
                        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 5)
                        {
                            long user    = Parse64(p, 1);
                            long nice    = Parse64(p, 2);
                            long system  = Parse64(p, 3);
                            long idle    = Parse64(p, 4);
                            long iowait  = Parse64(p, 5);
                            long irq     = Parse64(p, 6);
                            long softirq = Parse64(p, 7);

                            long total     = user + nice + system + idle + iowait + irq + softirq;
                            long idleTotal = idle + iowait;

                            if (_prevTotal > 0)
                            {
                                long dt = total - _prevTotal;
                                long di = idleTotal - _prevIdle;
                                stats.CpuPercent = dt > 0
                                    ? Math.Round((1.0 - (double)di / dt) * 100.0, 1)
                                    : 0.0;
                            }
                            // always update prev (first sample gives no CPU %, that's fine)
                            _prevIdle  = idleTotal;
                            _prevTotal = total;
                        }
                        continue;
                    }

                    // ── free -b Mem: line ────────────────────────────────────
                    if (line.StartsWith("Mem:"))
                    {
                        // Mem: total used free shared buff/cache available
                        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3 &&
                            long.TryParse(p[1], out long total) &&
                            long.TryParse(p[2], out long used) &&
                            total > 0)
                        {
                            stats.RamPercent = Math.Round((double)used / total * 100.0, 1);
                        }
                        continue;
                    }

                    // ── df last line (contains % somewhere) ──────────────────
                    if (line.Contains('%'))
                    {
                        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (token.EndsWith('%') &&
                                double.TryParse(token.TrimEnd('%'), out double pct))
                            {
                                stats.DiskPercent = pct;
                                break;
                            }
                        }
                    }
                }
            }

            private static long Parse64(string[] parts, int idx)
                => idx < parts.Length && long.TryParse(parts[idx], out var v) ? v : 0;
        }
    }
}
