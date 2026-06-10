using System;

namespace SwellSSH.Models
{
    public enum PortForwardType
    {
        Local,
        Remote,
        Dynamic
    }

    /// <summary>
    /// Represents an SSH port forwarding rule.
    /// Local:   -L BindPort:TargetHost:TargetPort
    /// Remote:  -R BindPort:TargetHost:TargetPort
    /// Dynamic: -D BindPort (SOCKS5 proxy)
    /// </summary>
    public class PortForwardRule
    {
        public PortForwardType Type { get; set; } = PortForwardType.Local;
        
        /// <summary>Local address to bind (e.g. 127.0.0.1 or 0.0.0.0). Applies to Local and Dynamic.</summary>
        public string BindAddress { get; set; } = "127.0.0.1";
        
        /// <summary>Local or Remote port to listen on.</summary>
        public int BindPort { get; set; } = 8080;
        
        /// <summary>Target host for Local/Remote forwards. Ignored for Dynamic.</summary>
        public string TargetHost { get; set; } = "127.0.0.1";
        
        /// <summary>Target port for Local/Remote forwards. Ignored for Dynamic.</summary>
        public int TargetPort { get; set; } = 80;
        
        /// <summary>Whether this rule should be active upon connection.</summary>
        public bool Enabled { get; set; } = true;
    }
}
