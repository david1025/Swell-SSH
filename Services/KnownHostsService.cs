using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwellSSH.Services
{
    /// <summary>One trusted host-key entry persisted in known_hosts.json.</summary>
    public class KnownHostEntry
    {
        public string Algorithm   { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public DateTime TrustedAt { get; set; }
    }

    /// <summary>
    /// Reads / writes a local known_hosts.json similar to OpenSSH's known_hosts.
    /// Key format: "host:port"
    /// </summary>
    public class KnownHostsService
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SwellSSH", "known_hosts.json");

        private Dictionary<string, KnownHostEntry> _entries = new();
        private readonly object _lock = new();

        public KnownHostsService() => Load();

        // ── Query ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns: true = trusted and fingerprint matches,
        ///          false = known host but fingerprint changed (MITM warning),
        ///          null = never seen before.
        /// </summary>
        public bool? Check(string host, int port, string algorithm, string fingerprint)
        {
            lock (_lock)
            {
                var key = $"{host}:{port}";
                if (!_entries.TryGetValue(key, out var e)) return null;
                return e.Algorithm == algorithm && e.Fingerprint == fingerprint;
            }
        }

        // ── Mutate ────────────────────────────────────────────────────────────

        public void Trust(string host, int port, string algorithm, string fingerprint)
        {
            lock (_lock)
            {
                _entries[$"{host}:{port}"] = new KnownHostEntry
                {
                    Algorithm   = algorithm,
                    Fingerprint = fingerprint,
                    TrustedAt   = DateTime.UtcNow
                };
            }
            Save();
        }

        public void Remove(string host, int port)
        {
            lock (_lock) { _entries.Remove($"{host}:{port}"); }
            Save();
        }

        // ── Persistence ───────────────────────────────────────────────────────

        private void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return;
                    var json = File.ReadAllText(FilePath);
                    _entries = JsonSerializer.Deserialize<Dictionary<string, KnownHostEntry>>(json) ?? new();
                }
                catch { _entries = new(); }
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    var opts = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, opts));
                }
                catch { /* silently ignore write errors */ }
            }
        }
    }
}
