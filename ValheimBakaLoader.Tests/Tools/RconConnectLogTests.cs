using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// What the RCON client says out loud while a world is up.
    /// <para>
    /// WHY THIS EXISTS. The roster poller opens a fresh RCON connection every few seconds for
    /// as long as a server runs, and the client wrote "RCON connected to ..." at Information on
    /// every single one of them. On the owner's box that one line was ninety two percent of the
    /// whole application log, and the Recent log on screen showed nothing else. A host reading
    /// the log to work out what went wrong could not see past it.
    /// </para>
    /// <para>
    /// The rule these hold is: one Information line per change of state. Coming up says so
    /// once, going away says so once, and the hundreds of routine re-connects in between go to
    /// Debug where they belong.
    /// </para>
    /// <para>
    /// In the wall-clock collection because every connection here is a real socket against the
    /// client's own five second connect and read budgets, answered by a fake server that lives
    /// on the thread pool. On the two-core runner, in the first half minute of the run, that
    /// pool could not give the fake server a thread inside the budget, so "connection 1 did not
    /// complete" held the 1.2.6 release. The budgets are the product's and stay as they are.
    /// </para>
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class RconConnectLogTests
    {
        /// <summary>
        /// A listener that plays the part of the RCON mod: it accepts a connection, reads the
        /// auth packet, replies "Login Success" and closes the socket, which is exactly the
        /// one-request-per-socket shape the real mod has.
        /// </summary>
        private sealed class FakeRcon : IDisposable
        {
            private readonly TcpListener Listener;
            private readonly CancellationTokenSource Stopping = new();
            private readonly Task Loop;

            public FakeRcon()
            {
                Listener = new TcpListener(IPAddress.Loopback, 0);
                Listener.Start();
                Port = ((IPEndPoint)Listener.LocalEndpoint).Port;
                Loop = Task.Run(ServeAsync);
            }

            public int Port { get; }

            private async Task ServeAsync()
            {
                while (!Stopping.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await Listener.AcceptTcpClientAsync(); }
                    catch { return; }

                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var packet = await RconClient.ReadPacketAsync(stream);
                            if (packet == null) continue;
                            await RconClient.SendPacketAsync(stream, packet.Id, 2, "Login Success");
                        }
                        catch
                        {
                            // A torn-down socket is the mod's own behaviour; nothing to say.
                        }
                    }
                }
            }

            public void Dispose()
            {
                Stopping.Cancel();
                try { Listener.Stop(); } catch { }
                try { Loop.Wait(TimeSpan.FromSeconds(5)); } catch { }
                Stopping.Dispose();
            }
        }

        /// <summary>
        /// The application logger's shape without its file sink: it keeps every line beside the
        /// level it was written at.
        /// </summary>
        private sealed class Remembering : IApplicationLogger
        {
            public List<(LogEventLevel Level, string Text)> Lines { get; } = new();

            public event Action<string> LogReceived;

            public IEnumerable<string> LogBuffer => Lines.Select(l => l.Text);

            public void Write(LogEvent logEvent)
            {
                if (logEvent == null) return;
                var text = logEvent.RenderMessage();
                Lines.Add((logEvent.Level, text));
                LogReceived?.Invoke(text);
            }

            public bool IsEnabled(LogEventLevel level) => true;

            public int Count(LogEventLevel level, string fragment)
                => Lines.Count(l => l.Level == level && l.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        [Fact(Timeout = 120000)]
        public async Task Fifty_connections_in_a_row_write_one_connected_line()
        {
            using var server = new FakeRcon();
            var logger = new Remembering();
            using var client = new RconClient(logger);

            for (var i = 0; i < 50; i++)
            {
                var ok = await client.ConnectAsync("127.0.0.1", server.Port, "secret");
                Assert.True(ok, "connection " + (i + 1) + " did not complete");
                client.Disconnect();
            }

            Assert.Equal(1, logger.Count(LogEventLevel.Information, "RCON connected"));
            Assert.Equal(49, logger.Count(LogEventLevel.Debug, "RCON reconnected"));
        }

        /// <summary>
        /// The other half of the rule: when a connection that WAS up goes away the host is told
        /// once, and every retry after that is quiet until something changes again.
        /// </summary>
        [Fact(Timeout = 120000)]
        public async Task Losing_the_connection_is_said_once_and_coming_back_is_said_once()
        {
            var logger = new Remembering();
            using var client = new RconClient(logger);

            int port;
            using (var server = new FakeRcon())
            {
                port = server.Port;
                Assert.True(await client.ConnectAsync("127.0.0.1", port, "secret"));
            }

            // The listener is gone. Ten retries against the closed port.
            for (var i = 0; i < 10; i++)
            {
                Assert.False(await client.ConnectAsync("127.0.0.1", port, "secret"));
            }

            Assert.Equal(1, logger.Count(LogEventLevel.Information, "RCON lost"));
            Assert.Equal(0, logger.Count(LogEventLevel.Warning, "RCON connection error"));

            // Nothing above the Debug floor beyond the one "connected" and the one "lost".
            var loud = logger.Lines.Count(l => l.Level >= LogEventLevel.Information);
            Assert.Equal(2, loud);
        }
    }
}
