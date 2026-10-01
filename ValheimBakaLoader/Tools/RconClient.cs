using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public interface IRconClient
    {
        bool IsConnected { get; }

        Task<bool> ConnectAsync(string host, int port, string password);

        Task<string> SendCommandAsync(string command);

        void Disconnect();
    }

    /// <summary>
    /// RCON client for the AviiNL "rcon" BepInEx mod (https://github.com/AviiNL/BepInEx.rcon),
    /// which is the RCON listener used by Valheim dedicated servers together with
    /// JereKuusela's Rcon_Commands + Server_devcommands (which provide the "broadcast" command).
    ///
    /// That mod uses a Source-RCON-style packet frame but with two important quirks that this
    /// client is built around:
    ///   1. The server closes the TCP socket after EVERY response (including the auth response),
    ///      so a single connection can only carry one request. We therefore open a fresh
    ///      connection per command instead of using one persistent session.
    ///   2. Command packets are executed without checking for a prior successful login, so we
    ///      only perform the auth handshake once (in <see cref="ConnectAsync"/>) to validate the
    ///      password / connectivity for the user, and send subsequent commands directly.
    ///
    /// Packet frame (little-endian): [length:int32][requestId:int32][type:int32][body...][0][0]
    /// where length counts everything after the length field. Type 3 = AUTH, 2 = EXEC/AUTH_RESPONSE.
    /// </summary>
    public class RconClient : IRconClient, IDisposable
    {
        // Source RCON packet types
        private const int SERVERDATA_AUTH = 3;
        private const int SERVERDATA_AUTH_RESPONSE = 2;
        private const int SERVERDATA_EXECCOMMAND = 2;

        // The AviiNL mod writes header integers a single byte at a time, so a request id only
        // round-trips correctly when it fits in one byte. Keep ids in the 1..255 range.
        private const int MaxRequestId = 255;

        private const int ConnectTimeoutMs = 5000;
        private const int IoTimeoutMs = 5000;

        /// <summary>
        /// How packet bodies are written and read. UTF-8 with no byte-order mark, which is
        /// what the companion plugin on the other end writes and reads too.
        /// <para>
        /// This was ASCII on both ends until 1.1.0, and ASCII turns every letter outside the
        /// first 128 into a question mark. That cost real text: a player name with Cyrillic,
        /// Japanese or accented letters came back as "???" in the roster and went out as
        /// "???" in a spawn, tp or kick, and a broadcast or restart warning written in any
        /// of those alphabets reached the world unreadable. UTF-8 carries all of it and is
        /// byte-for-byte the same as ASCII for plain English, so nothing that worked before
        /// changes.
        /// </para>
        /// </summary>
        internal static readonly Encoding BodyEncoding = new UTF8Encoding(false);

        private readonly IApplicationLogger Logger;
        private readonly SemaphoreSlim SendLock = new(1, 1);

        private string Host;
        private int Port;
        private string Password;
        private bool Validated;
        private int RequestId;

        // The roster poller opens a fresh connection every few seconds for as long as a server
        // runs, and every one of those used to write "RCON connected" at Information. On a box
        // that leaves a world up all evening that one line was most of the application log and
        // most of the Recent log on screen. These two fields remember what was last said out
        // loud, so the routine re-connects are Verbose (the Detailed log switch) and only a real
        // change of state reaches the host: one "connected" when it comes up, one "lost" when it
        // goes away. Debug was tried first and, being the level every install writes at, it
        // still filled half the log.
        private string ReportedEndpoint;
        private string ReportedState;

        public RconClient(IApplicationLogger appLogger)
        {
            Logger = appLogger;
        }

        public bool IsConnected => Validated;

        /// <summary>
        /// Validates connectivity and the RCON password by performing a single auth handshake,
        /// then stores the connection details for subsequent per-command connections.
        /// </summary>
        public async Task<bool> ConnectAsync(string host, int port, string password)
        {
            Disconnect();

            Host = host;
            Port = port;
            Password = password ?? string.Empty;

            try
            {
                using var client = new TcpClient();
                using (var connectCts = new CancellationTokenSource(ConnectTimeoutMs))
                {
                    await client.ConnectAsync(host, port, connectCts.Token);
                }

                using var stream = client.GetStream();

                var authId = NextRequestId();
                await SendPacketAsync(stream, authId, SERVERDATA_AUTH, Password);

                // The mod replies "Login Success" (echoing the request id) or "Login Failed"
                // (with request id -1), then closes the socket.
                var packet = await ReadPacketAsync(stream);
                if (packet == null)
                {
                    ReportLost(host, port, "no-response", "RCON authentication failed: no response from {host}:{port}");
                    return false;
                }

                if (packet.Body != null && packet.Body.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ReportLost(host, port, "bad-password", "RCON authentication failed: bad password for {host}:{port}");
                    return false;
                }

                Validated = true;
                if (NoteState(host, port, "connected"))
                {
                    Logger.Information("RCON connected to {host}:{port}", host, port);
                }
                else
                {
                    // Verbose, not Debug: Debug is the level every install runs at, and the roster
                    // poller reconnects every five seconds for as long as a world is up, so at Debug
                    // this one line was still half of the application log and most of the Saga on
                    // screen. It is only worth reading while chasing a problem, which is what the
                    // Detailed log switch is for.
                    Logger.Verbose("RCON reconnected to {host}:{port}", host, port);
                }

                return true;
            }
            catch (Exception e)
            {
                var wasConnected = WasConnected(host, port);
                if (!NoteState(host, port, FailureState(e)))
                {
                    Logger.Verbose("RCON connection error ({host}:{port}): {message}", host, port, e.Message);
                }
                else if (wasConnected)
                {
                    Logger.Information("RCON lost to {host}:{port}: {message}", host, port, e.Message);
                }
                else
                {
                    Logger.Warning("RCON connection error ({host}:{port}): {message}", host, port, e.Message);
                }

                return false;
            }
        }

        /// <summary>
        /// Writes one of the connection failures at the level a host deserves: an Information
        /// "lost" line the first time a connection that was up goes away, the warning once when
        /// it never came up at all, and Verbose for every repeat of the same state at the same
        /// address, so a world that is down does not write a line every five seconds either.
        /// </summary>
        private void ReportLost(string host, int port, string state, string template)
        {
            var wasConnected = WasConnected(host, port);
            if (!NoteState(host, port, state))
            {
                Logger.Verbose(template, host, port);
                return;
            }

            if (wasConnected)
            {
                Logger.Information("RCON lost to {host}:{port}", host, port);
                return;
            }

            Logger.Warning(template, host, port);
        }

        /// <summary>
        /// The state name one connection failure is remembered by.
        /// <para>
        /// It used to be the exception's MESSAGE, and a message is not an identity: the same
        /// address refusing the same connection can word itself differently from one attempt
        /// to the next, and a socket error carries the endpoint in its text. Every such
        /// variation read as a NEW state, so a server that had been down for an hour went on
        /// writing a Warning a host reads, over and over, for one thing being wrong once. The
        /// exception's type and, where there is one, the socket error code are what actually
        /// say which failure this is.
        /// </para>
        /// </summary>
        internal static string FailureState(Exception problem)
        {
            if (problem == null) return "error";

            var socket = problem as SocketException
                ?? problem.InnerException as SocketException;

            return socket != null
                ? "error:" + problem.GetType().Name + ":" + (int)socket.SocketErrorCode
                : "error:" + problem.GetType().Name;
        }

        private bool WasConnected(string host, int port)
        {
            return ReportedState == "connected" && ReportedEndpoint == Endpoint(host, port);
        }

        /// <summary>
        /// Records the connection state for an address and answers whether it changed. A change
        /// is a different state, or the same state reached at a different address.
        /// </summary>
        private bool NoteState(string host, int port, string state)
        {
            var endpoint = Endpoint(host, port);
            if (ReportedEndpoint == endpoint && ReportedState == state) return false;

            ReportedEndpoint = endpoint;
            ReportedState = state;
            return true;
        }

        private static string Endpoint(string host, int port)
        {
            return (host ?? string.Empty) + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Opens a fresh connection, sends a single command, reads the (best-effort) response,
        /// and lets the server close the socket. Auth is not re-sent because the mod does not
        /// gate commands on it and the auth response would close the socket prematurely.
        /// </summary>
        public async Task<string> SendCommandAsync(string command)
        {
            if (!Validated)
            {
                Logger.Warning("Cannot send RCON command, not connected: {command}", command);
                return null;
            }

            await SendLock.WaitAsync();
            try
            {
                using var client = new TcpClient();
                using (var connectCts = new CancellationTokenSource(ConnectTimeoutMs))
                {
                    await client.ConnectAsync(Host, Port, connectCts.Token);
                }

                using var stream = client.GetStream();

                var id = NextRequestId();
                await SendPacketAsync(stream, id, SERVERDATA_EXECCOMMAND, command);

                // The command has executed server-side by the time it responds. Reading the
                // response is best-effort; the server may close the socket immediately, and
                // long responses can have a malformed length header (a mod quirk), so any
                // read problem is non-fatal. We drain ALL packets until the server closes the
                // socket and concatenate them: some commands (e.g. "pos") emit a timestamped
                // log line in one packet and the actual result in a later packet, so reading
                // only the first packet would miss the data we care about.
                var body = new StringBuilder();

                // Defensive cap: each ReadPacketAsync already has a 5s timeout, but a
                // server that keeps the socket open and streams data forever could grow
                // this buffer unbounded. Stop draining once we've collected far more than
                // any legitimate command response would ever produce.
                const int MaxResponseChars = 64 * 1024;
                try
                {
                    while (body.Length < MaxResponseChars)
                    {
                        var packet = await ReadPacketAsync(stream);
                        if (packet == null) break; // socket closed / short read
                        if (!string.IsNullOrEmpty(packet.Body)) body.Append(packet.Body);
                    }
                }
                catch
                {
                    // Partial/garbled tail is fine - return whatever we managed to read.
                }

                return body.ToString();
            }
            catch (Exception e)
            {
                Logger.Warning("RCON command error: {message}", e.Message);
                return null;
            }
            finally
            {
                SendLock.Release();
            }
        }

        public void Disconnect()
        {
            Validated = false;
        }

        public void Dispose()
        {
            Disconnect();
            SendLock.Dispose();
            GC.SuppressFinalize(this);
        }

        #region Protocol helpers

        private int NextRequestId()
        {
            // Keep ids in 1..255 so they survive the mod's single-byte header encoding
            RequestId = (RequestId % MaxRequestId) + 1;
            return RequestId;
        }

        internal static async Task SendPacketAsync(Stream stream, int id, int type, string body)
        {
            // Counted in BYTES, never in characters: one letter outside the first 128 is two
            // or more bytes, and the length field is what the reader on the other end trusts.
            var bodyBytes = BodyEncoding.GetBytes(body ?? string.Empty);

            // length covers: id(4) + type(4) + body + body null terminator(1) + trailing null(1)
            var length = 4 + 4 + bodyBytes.Length + 2;
            var packet = new byte[4 + length];

            var offset = 0;
            WriteInt32(packet, ref offset, length);
            WriteInt32(packet, ref offset, id);
            WriteInt32(packet, ref offset, type);
            Buffer.BlockCopy(bodyBytes, 0, packet, offset, bodyBytes.Length);
            offset += bodyBytes.Length;
            packet[offset++] = 0; // body terminator
            packet[offset] = 0;   // trailing null

            using var cts = new CancellationTokenSource(IoTimeoutMs);
            await stream.WriteAsync(packet.AsMemory(0, packet.Length), cts.Token);
            await stream.FlushAsync(cts.Token);
        }

        internal static async Task<RconPacket> ReadPacketAsync(Stream stream)
        {
            using var cts = new CancellationTokenSource(IoTimeoutMs);

            var header = new byte[4];
            if (!await ReadExactAsync(stream, header, 4, cts.Token)) return null;

            var length = BitConverter.ToInt32(header, 0);
            if (length < 10 || length > 4110) return null; // sanity bound (Source max packet ~4096 + header)

            var payload = new byte[length];
            if (!await ReadExactAsync(stream, payload, length, cts.Token)) return null;

            var id = BitConverter.ToInt32(payload, 0);
            var type = BitConverter.ToInt32(payload, 4);
            // body is everything after id+type, minus the two trailing null bytes
            var bodyLength = length - 4 - 4 - 2;
            var body = bodyLength > 0 ? BodyEncoding.GetString(payload, 8, bodyLength) : string.Empty;

            return new RconPacket { Id = id, Type = type, Body = body };
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            var read = 0;
            while (read < count)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), token);
                if (n == 0) return false; // connection closed
                read += n;
            }
            return true;
        }

        private static void WriteInt32(byte[] buffer, ref int offset, int value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            buffer[offset++] = (byte)((value >> 16) & 0xFF);
            buffer[offset++] = (byte)((value >> 24) & 0xFF);
        }

        internal class RconPacket
        {
            public int Id { get; set; }
            public int Type { get; set; }
            public string Body { get; set; }
        }

        #endregion
    }
}
