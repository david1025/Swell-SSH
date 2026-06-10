using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace SwellSSH.Services
{
    // ── SSH agent wire-protocol constants ─────────────────────────────────────

    internal static class AgentMsg
    {
        public const byte Failure           = 5;
        public const byte RequestIdentities = 11;
        public const byte IdentitiesAnswer  = 12;
        public const byte SignRequest       = 13;
        public const byte SignResponse      = 14;

        /// <summary>Flag to request rsa-sha2-256 instead of the deprecated ssh-rsa/SHA-1.</summary>
        public const uint FlagRsaSha2_256   = 2;
    }

    // ── Public data types ─────────────────────────────────────────────────────

    /// <summary>An SSH key identity returned by the agent (public blob + comment).</summary>
    public sealed class AgentIdentity
    {
        /// <summary>Raw SSH public-key blob in SSH wire format (begins with key-type string).</summary>
        public byte[] KeyBlob     { get; }

        /// <summary>Human-readable comment attached to the key (e.g. "user@host").</summary>
        public string Comment     { get; }

        /// <summary>SSH key-type name extracted from KeyBlob, e.g. "ssh-rsa", "ssh-ed25519".</summary>
        public string KeyTypeName { get; }

        internal AgentIdentity(byte[] keyBlob, string comment, string keyTypeName)
        {
            KeyBlob     = keyBlob;
            Comment     = comment;
            KeyTypeName = keyTypeName;
        }
    }

    // ── Agent service ─────────────────────────────────────────────────────────

    /// <summary>
    /// Detects and communicates with the Windows OpenSSH Authentication Agent
    /// via the <c>openssh-ssh-agent</c> named pipe.
    /// </summary>
    public static class SshAgentService
    {
        private const string OpenSshPipeName = "openssh-ssh-agent";

        // ── Detection ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> if the Windows OpenSSH Agent named pipe is reachable.
        /// Uses a 500 ms timeout so callers from UI threads stay responsive.
        /// </summary>
        public static bool IsOpenSshAgentAvailable()
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", OpenSshPipeName, PipeDirection.InOut);
                pipe.Connect(500);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Opens a connection to the OpenSSH Agent pipe.
        /// Caller owns the returned stream and must dispose it.
        /// Throws <see cref="InvalidOperationException"/> if the agent is unavailable.
        /// </summary>
        public static NamedPipeClientStream OpenAgentPipe(int timeoutMs = 3000)
        {
            var pipe = new NamedPipeClientStream(".", OpenSshPipeName, PipeDirection.InOut);
            try
            {
                pipe.Connect(timeoutMs);
                return pipe;
            }
            catch (Exception ex)
            {
                pipe.Dispose();
                throw new InvalidOperationException(
                    $"无法连接到 Windows OpenSSH Agent：{ex.Message}\n\n" +
                    "请在「服务（services.msc）」中将「OpenSSH Authentication Agent」" +
                    "设为「自动」启动类型并启动该服务。");
            }
        }

        // ── Wire protocol — request identities ───────────────────────────────

        /// <summary>
        /// Asks the agent for all loaded key identities.
        /// The <paramref name="agentStream"/> must be an open pipe to the agent.
        /// </summary>
        public static IReadOnlyList<AgentIdentity> RequestIdentities(Stream agentStream)
        {
            // SSH2_AGENTC_REQUEST_IDENTITIES
            SendFrame(agentStream, new byte[] { AgentMsg.RequestIdentities });

            var resp = ReceiveFrame(agentStream);
            if (resp.Length == 0 || resp[0] != AgentMsg.IdentitiesAnswer)
                throw new InvalidOperationException(
                    $"Agent 返回意外响应码 {(resp.Length > 0 ? resp[0] : 0)}，期望 {AgentMsg.IdentitiesAnswer}。");

            int pos   = 1;
            int count = ReadInt32(resp, ref pos);
            var list  = new List<AgentIdentity>(count);

            for (int i = 0; i < count; i++)
            {
                var  keyBlob      = ReadSshString(resp, ref pos);
                var  commentBytes = ReadSshString(resp, ref pos);
                string comment    = Encoding.UTF8.GetString(commentBytes);

                // First field in the key blob is the key-type string
                int blobPos  = 0;
                string ktype = Encoding.ASCII.GetString(ReadSshString(keyBlob, ref blobPos));

                list.Add(new AgentIdentity(keyBlob, comment, ktype));
            }

            return list;
        }

        // ── Wire protocol — sign ──────────────────────────────────────────────

        /// <summary>
        /// Asks the agent to sign <paramref name="data"/> with the key identified by
        /// <paramref name="keyBlob"/>. Returns the raw signature bytes (unwrapped from
        /// the sig-blob envelope so they can be passed directly to SSH.NET).
        /// </summary>
        public static byte[] Sign(Stream agentStream, byte[] keyBlob,
                                  string keyTypeName, byte[] data)
        {
            // Upgrade RSA from SHA-1 to SHA-256 (modern servers deprecate ssh-rsa/SHA-1)
            uint flags = keyTypeName == "ssh-rsa" ? AgentMsg.FlagRsaSha2_256 : 0u;

            using var ms = new MemoryStream();
            ms.WriteByte(AgentMsg.SignRequest);
            WriteSshString(ms, keyBlob);
            WriteSshString(ms, data);
            WriteUInt32(ms, flags);
            SendFrame(agentStream, ms.ToArray());

            var resp = ReceiveFrame(agentStream);

            if (resp.Length == 0 || resp[0] == AgentMsg.Failure)
                throw new InvalidOperationException(
                    "SSH Agent 拒绝签名。\n" +
                    "请确认：① 密钥已通过 ssh-add 加入 Agent；② 服务器接受该密钥。");

            if (resp[0] != AgentMsg.SignResponse)
                throw new InvalidOperationException($"Agent 返回意外签名响应码：{resp[0]}");

            // sig_blob = string:sig_algorithm_name + string:raw_signature
            int pos     = 1;
            var sigBlob = ReadSshString(resp, ref pos);   // outer length-prefixed blob

            int sbPos   = 0;
            ReadSshString(sigBlob, ref sbPos);             // skip sig_algorithm_name
            return ReadSshString(sigBlob, ref sbPos);      // raw signature bytes
        }

        // ── SSH binary-packet helpers ─────────────────────────────────────────

        private static void SendFrame(Stream s, byte[] payload)
        {
            int n = payload.Length;
            s.Write(new byte[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }, 0, 4);
            s.Write(payload, 0, n);
            s.Flush();
        }

        private static byte[] ReceiveFrame(Stream s)
        {
            var hdr = new byte[4];
            ReadFull(s, hdr, 4);
            int len = (int)((uint)hdr[0] << 24 | (uint)hdr[1] << 16 | (uint)hdr[2] << 8 | hdr[3]);
            var buf = new byte[len];
            ReadFull(s, buf, len);
            return buf;
        }

        private static void ReadFull(Stream s, byte[] buf, int needed)
        {
            int done = 0;
            while (done < needed)
            {
                int r = s.Read(buf, done, needed - done);
                if (r == 0) throw new EndOfStreamException("Agent pipe closed unexpectedly.");
                done += r;
            }
        }

        private static int ReadInt32(byte[] buf, ref int pos)
        {
            int v = (int)((uint)buf[pos] << 24 | (uint)buf[pos+1] << 16
                        | (uint)buf[pos+2] << 8  | buf[pos+3]);
            pos += 4;
            return v;
        }

        private static byte[] ReadSshString(byte[] buf, ref int pos)
        {
            int len = ReadInt32(buf, ref pos);
            var data = new byte[len];
            Array.Copy(buf, pos, data, 0, len);
            pos += len;
            return data;
        }

        private static void WriteSshString(MemoryStream ms, byte[] data)
        {
            WriteUInt32(ms, (uint)data.Length);
            ms.Write(data, 0, data.Length);
        }

        private static void WriteUInt32(MemoryStream ms, uint v)
        {
            ms.WriteByte((byte)(v >> 24));
            ms.WriteByte((byte)(v >> 16));
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)v);
        }
    }

    // ── SSH.NET integration ───────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="HostAlgorithm"/> whose <c>Sign</c> method delegates to an
    /// SSH agent pipe, keeping the private key inside the agent at all times.
    /// </summary>
    internal sealed class AgentHostAlgorithm : HostAlgorithm
    {
        private readonly Stream _pipe;
        private readonly byte[] _keyBlob;
        private readonly string _keyTypeName;

        /// <param name="signatureAlgorithmName">
        ///   Algorithm name for the SSH auth packet
        ///   (e.g. "rsa-sha2-256" or "ssh-ed25519").
        /// </param>
        public AgentHostAlgorithm(string signatureAlgorithmName,
                                  byte[] keyBlob, string keyTypeName, Stream pipe)
            : base(signatureAlgorithmName)
        {
            _pipe        = pipe;
            _keyBlob     = keyBlob;
            _keyTypeName = keyTypeName;
        }

        /// <summary>SSH-wire-format public key blob — exactly what the agent returned.</summary>
        public override byte[] Data => _keyBlob;

        /// <summary>Delegates signing to the agent; private key never leaves the agent process.</summary>
        public override byte[] Sign(byte[] data)
            => SshAgentService.Sign(_pipe, _keyBlob, _keyTypeName, data);

        /// <summary>Not required for client auth; only called for server host-key verification.</summary>
        public override bool VerifySignature(byte[] data, byte[] signature)
            => throw new NotSupportedException(
                "AgentHostAlgorithm does not support server signature verification.");
    }

    /// <summary>
    /// Wraps a single <see cref="AgentIdentity"/> as an SSH.NET <see cref="IPrivateKeySource"/>.
    /// </summary>
    internal sealed class AgentKeySource : IPrivateKeySource
    {
        private readonly AgentHostAlgorithm _algorithm;

        public AgentKeySource(AgentIdentity identity, Stream agentPipe)
        {
            // RSA keys: advertise "rsa-sha2-256" so modern servers accept the auth
            string sigAlg = identity.KeyTypeName == "ssh-rsa"
                ? "rsa-sha2-256"
                : identity.KeyTypeName;

            _algorithm = new AgentHostAlgorithm(sigAlg, identity.KeyBlob, identity.KeyTypeName, agentPipe);
        }

        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms
            => new HostAlgorithm[] { _algorithm };
    }
}
