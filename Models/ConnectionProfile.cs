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

        public DateTime LastConnected { get; set; } = DateTime.MinValue;
    }
}
