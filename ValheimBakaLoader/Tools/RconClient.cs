using System;
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

        private readonly IApplicationLogger Logger;
        private readonly SemaphoreSlim SendLock = new(1, 1);

        private string Host;
        private int Port;
        private string Password;
        private bool Validated;
        private int RequestId;

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
                    Logger.Warning("RCON authentication failed: no response from {host}:{port}", host, port);
                    return false;
                }

                if (packet.Body != null && packet.Body.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.Warning("RCON authentication failed: bad password for {host}:{port}", host, port);
                    return false;
                }

                Validated = true;
                Logger.Information("RCON connected to {host}:{port}", host, port);
                return true;
            }
            catch (Exception e)
            {
                Logger.Warning("RCON connection error ({host}:{port}): {message}", host, port, e.Message);
                return false;
            }
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

        private static async Task SendPacketAsync(NetworkStream stream, int id, int type, string body)
        {
            var bodyBytes = Encoding.ASCII.GetBytes(body ?? string.Empty);

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

        private static async Task<RconPacket> ReadPacketAsync(NetworkStream stream)
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
            var body = bodyLength > 0 ? Encoding.ASCII.GetString(payload, 8, bodyLength) : string.Empty;

            return new RconPacket { Id = id, Type = type, Body = body };
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken token)
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

        private class RconPacket
        {
            public int Id { get; set; }
            public int Type { get; set; }
            public string Body { get; set; }
        }

        #endregion
    }
}
