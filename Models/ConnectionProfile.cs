using System;

namespace SwellSSH.Models
{
    /// <summary>
    /// SSH connection profile — persisted to JSON via ConnectionStorage.
    /// Password is stored encrypted with Windows DPAPI (ProtectedData).
    /// </summary>
    public class ConnectionProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "New Connection";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "";
        
        /// <summary>Group/Folder for UI organization</summary>
        public string Group { get; set; } = "默认分组";

        /// <summary>Password or PrivateKey</summary>
        public string AuthType { get; set; } = "Password";

        /// <summary>DPAPI-encrypted Base64 of the password. Never plaintext.</summary>
        public string EncryptedPassword { get; set; } = "";

        /// <summary>Absolute path to private key file (OpenSSH format).</summary>
        public string PrivateKeyPath { get; set; } = "";

        /// <summary>Passphrase for private key, DPAPI-encrypted Base64. Empty if none.</summary>
        public string EncryptedPassphrase { get; set; } = "";

        public int TerminalCols { get; set; } = 120;
        public int TerminalRows { get; set; } = 30;

        /// <summary>
        /// SSH Keepalive 心跳间隔（秒）。0 = 使用全局设置或关闭。
        /// 推荐值 30~60，防止 NAT 或防火墙因空闲断开连接。
        /// </summary>
        public int KeepAliveIntervalSeconds { get; set; } = 60;

        /// <summary>Whether the sidebar monitoring widget is active for this server.</summary>
        public bool EnableMonitoring { get; set; } = false;

        /// <summary>How often (seconds) the monitoring service polls. Minimum 3 s.</summary>
        public int MonitorIntervalSeconds { get; set; } = 10;

        public DateTime LastConnected { get; set; } = DateTime.MinValue;
    }
}
