using System;

namespace SwellSSH.Models
{
    /// <summary>
    /// Server performance metrics from one monitoring poll.
    /// All percentage values are -1 when unknown / not yet available.
    /// </summary>
    public class ServerStats
    {
        public string ConnectionId { get; set; } = "";

        /// <summary>CPU usage 0‑100 %. -1 means first sample (no delta yet).</summary>
        public double CpuPercent { get; set; } = -1;

        /// <summary>RAM usage 0‑100 %.</summary>
        public double RamPercent { get; set; } = -1;

        /// <summary>Root-filesystem disk usage 0‑100 %.</summary>
        public double DiskPercent { get; set; } = -1;

        public DateTime UpdatedAt { get; set; } = DateTime.MinValue;

        public bool HasError { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>True when all three metrics are available and there is no error.</summary>
        public bool IsAvailable => !HasError && CpuPercent >= 0 && RamPercent >= 0 && DiskPercent >= 0;
    }
}
