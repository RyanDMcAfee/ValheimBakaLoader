using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Detailed log: the dial, and the one line per web request it opens up.
    /// <para>
    /// WHY THIS EXISTS. Issue 18's reporter could not reach thunderstore.io from .NET while
    /// curl on the same machine went straight out, and the log said the read had failed and
    /// nothing else: not which address, not whether a proxy stood in front of it, not how
    /// long it sat there. The wire trace answers all three, and it is behind a switch
    /// because on a busy install those lines arrive fast.
    /// </para>
    /// <para>
    /// The rule that makes the whole arrangement safe is the last one here: with the log at
    /// its ordinary Debug level the handler writes NOTHING. A trace that leaked into every
    /// host's log would be the defect, not the feature.
    /// </para>
    /// </summary>
    public class DetailedLogTests
    {
        // ------------------------------------------------------- where a build with nothing on sits

        /// <summary>
        /// The level a build writes at with neither the command line nor the preference
        /// touched, and the sentence that says so.
        /// <para>
        /// Spelled out here rather than read off LogLevelControl, because a test that asked
        /// the code what the answer is would pass whatever the code said. A release has
        /// always written at Debug; a DEBUG build has always written at Verbose, and it is
        /// that half that went missing when the dial first stood in front of the sink.
        /// </para>
        /// </summary>
#if DEBUG
        private const LogEventLevel OffLevel = LogEventLevel.Verbose;
        private const string OffLine = "Log level Verbose (debug build)";
        private const string OffToggleLine = "Log level Verbose (Detailed log turned off)";
#else
        private const LogEventLevel OffLevel = LogEventLevel.Debug;
        private const string OffLine = "Log level Debug";
        private const string OffToggleLine = "Log level Debug (Detailed log turned off)";
#endif

        /// <summary>
        /// The dial STARTS where this build writes, before anything has applied anything.
        /// <para>
        /// This is the one the dial broke when it arrived. The level a build writes at used
        /// to be chosen by an <c>#if DEBUG</c> on the sink's own configuration, and a logger
        /// with a dial takes the other branch: a dial that started at Debug quietly turned
        /// every DEBUG build's Verbose into Debug, and nothing said so.
        /// </para>
        /// </summary>
        [Fact]
        public void The_dial_starts_where_this_build_has_always_written()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IStartupArgsProvider>(new StartupArgsProvider(Array.Empty<string>()));
            using var container = services.BuildServiceProvider();

            // Before Apply, before anything: the value it was constructed with.
            Assert.Equal(OffLevel, new LogLevelControl(container).Switch.MinimumLevel);
            Assert.Equal(OffLevel, LogLevelControl.DefaultLevel);
            Assert.Equal(OffLine, LogLevelControl.DefaultSentence);
        }

        // ------------------------------------------------------------------ a logger to read

        /// <summary>
        /// A Serilog logger that keeps what it was written, and answers IsEnabled off a dial
        /// exactly as the application logger does. It is the application logger's shape
        /// without its file sink: what is asserted here is what a caller writes and what the
        /// dial lets through, and neither of those is about a file.
        /// </summary>
        private sealed class Remembering : ILogger
        {
            public LoggingLevelSwitch Dial { get; } = new(LogEventLevel.Debug);

            public List<LogEvent> Events { get; } = new();

            public List<string> Lines => Events.Select(e => e.RenderMessage()).ToList();

            public void Write(LogEvent logEvent)
            {
                if (logEvent == null || !IsEnabled(logEvent.Level)) return;
                Events.Add(logEvent);
            }

            public bool IsEnabled(LogEventLevel level) => level >= Dial.MinimumLevel;
        }

        /// <summary>An inner transport that answers from a script and never touches a network.</summary>
        private sealed class Inner : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> Answer;

            public Inner(Func<HttpRequestMessage, HttpResponseMessage> answer)
            {
                Answer = answer;
            }

            public List<HttpRequestMessage> Seen { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Seen.Add(request);
                var answer = Answer(request);
                if (answer == null) throw new HttpRequestException(
                    "the site did not answer", new System.Net.Sockets.SocketException(10060));
                answer.RequestMessage ??= request;
                return Task.FromResult(answer);
            }
        }

        private static HttpResponseMessage Ok(string body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
            response.Content.Headers.ContentLength = body.Length;
            return response;
        }

        // ------------------------------------------------------------- (a) the level itself

        /// <summary>
        /// Release has always written at Debug and still does. The preference opens it to
        /// Verbose the moment it is applied, with no restart, because the dial the logger and
        /// its file sink both read is the thing that moved.
        /// </summary>
        [Fact]
        public void The_preference_moves_the_level_the_moment_it_is_applied()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IStartupArgsProvider>(new StartupArgsProvider(Array.Empty<string>()));
            using var container = services.BuildServiceProvider();
            var level = new LogLevelControl(container);

            Assert.Equal(OffLine, level.Apply(detailedLog: false));
            Assert.Equal(OffLevel, level.Current);
            Assert.Equal(OffLevel, level.Switch.MinimumLevel);

            Assert.Equal("Log level Verbose (Detailed log is on)", level.Apply(detailedLog: true));
            Assert.Equal(LogEventLevel.Verbose, level.Current);

            // And back again, which is the half a switch has to have to be a switch. Back to
            // where THIS build writes: on a release that is Debug, and a DEBUG build has
            // always written at Verbose whatever the switch says.
            Assert.Equal(OffLine, level.Apply(detailedLog: false));
            Assert.Equal(OffLevel, level.Current);
        }

        /// <summary>
        /// The command line wins for the whole session. A host whose window will not open is
        /// told to start the exe with it, so nothing inside the window may put it back.
        /// </summary>
        [Theory]
        [InlineData("--verbose")]
        [InlineData("--VERBOSE")]
        [InlineData("--Verbose")]
        public void The_command_line_switch_holds_verbose_however_it_is_spelled(string spelling)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IStartupArgsProvider>(
                new StartupArgsProvider(new[] { "Midgard", spelling }));
            using var container = services.BuildServiceProvider();
            var level = new LogLevelControl(container);

            Assert.True(level.ForcedByCommandLine);

            // With the preference OFF, which is the case the switch exists for.
            Assert.Equal("Log level Verbose (--verbose)", level.Apply(detailedLog: false));
            Assert.Equal(LogEventLevel.Verbose, level.Current);

            // And the page cannot close it: moving the preference changes the preference and
            // leaves the level where the command line put it.
            Assert.Equal("Log level Verbose (--verbose)", level.Toggled(detailedLog: false));
            Assert.Equal(LogEventLevel.Verbose, level.Current);

            // The bare argument still names the profile, which is what it always did.
            Assert.Equal("Midgard", container.GetRequiredService<IStartupArgsProvider>().ServerProfileName);
        }

        [Fact]
        public void Without_the_switch_nothing_is_forced()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IStartupArgsProvider>(
                new StartupArgsProvider(new[] { "Midgard", "--something-else" }));
            using var container = services.BuildServiceProvider();
            var level = new LogLevelControl(container);

            Assert.False(level.ForcedByCommandLine);
            Assert.Equal(OffLine, level.Apply(detailedLog: false));
        }

        /// <summary>
        /// The Upkeep card posts every switch on it in one object, so the Detailed log key
        /// arrives on a save that came from "Start with Windows" or any of the other ten.
        /// The key arriving is not the switch moving, and a line saying the level changed on
        /// a save that did not change it would be a false sentence in the one file this
        /// whole feature exists to make worth reading.
        /// </summary>
        [Fact]
        public void A_save_that_carries_the_switch_without_moving_it_is_not_a_move()
        {
            // A host who has never touched Detailed log, flipping "Start with Windows".
            var prefs = new UserPreferences();
            var card = JObject.Parse("{\"StartWithWindows\":true,\"DetailedLog\":false}");

            Assert.False(LogLevelControl.ApplySavedValue(prefs, card["DetailedLog"]));
            Assert.False(prefs.DetailedLog);

            // And a host who HAS it on, flipping one of the others, is not told it turned on.
            prefs.DetailedLog = true;
            var second = JObject.Parse("{\"ForceIPv4\":true,\"DetailedLog\":true}");
            Assert.False(LogLevelControl.ApplySavedValue(prefs, second["DetailedLog"]));
            Assert.True(prefs.DetailedLog);

            // Moving it is the one case that answers yes, both ways round.
            Assert.True(LogLevelControl.ApplySavedValue(
                prefs, JObject.Parse("{\"DetailedLog\":false}")["DetailedLog"]));
            Assert.False(prefs.DetailedLog);
            Assert.True(LogLevelControl.ApplySavedValue(
                prefs, JObject.Parse("{\"DetailedLog\":true}")["DetailedLog"]));
            Assert.True(prefs.DetailedLog);

            // A save that carried no such key at all changes nothing and says nothing.
            Assert.False(LogLevelControl.ApplySavedValue(prefs, null));
            Assert.True(prefs.DetailedLog);
        }

        /// <summary>
        /// And the save itself goes through that decision rather than round it. The bridge
        /// applies a key on PRESENCE, so this is the line that keeps the presence of the key
        /// from being read as a move.
        /// </summary>
        [Fact]
        public void The_save_records_a_move_only_through_the_comparison()
        {
            var bridge = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

            Assert.Contains(
                "Apply(\"DetailedLog\", v =>\n                    {\n"
                + "                        if (LogLevelControl.ApplySavedValue(prefs, v)) detailedLogMoved = true;",
                bridge);

            // The shape that shipped before: the key arriving counted as the switch moving.
            Assert.DoesNotContain("                        detailedLogMoved = true;", bridge);
        }

        /// <summary>Moving the switch by hand says so, once, in the log the host is about to read.</summary>
        [Fact]
        public void Moving_the_switch_writes_one_line_that_says_which_way_it_went()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IStartupArgsProvider>(new StartupArgsProvider(Array.Empty<string>()));
            using var container = services.BuildServiceProvider();
            var level = new LogLevelControl(container);

            Assert.Equal("Log level Verbose (Detailed log turned on)", level.Toggled(detailedLog: true));
            Assert.Equal(LogEventLevel.Verbose, level.Current);

            Assert.Equal(OffToggleLine, level.Toggled(detailedLog: false));
            Assert.Equal(OffLevel, level.Current);
        }

        /// <summary>
        /// The preference is off for anybody who has never seen it, and a preferences file
        /// that cannot be read answers off as well: a log nobody asked for must never be the
        /// one that grows.
        /// </summary>
        [Fact]
        public void The_preference_is_off_by_default_and_off_when_it_cannot_be_read()
        {
            Assert.False(new UserPreferences().DetailedLog);

            var services = new ServiceCollection();
            services.AddSingleton<IStartupArgsProvider>(new StartupArgsProvider(Array.Empty<string>()));
            using var container = services.BuildServiceProvider();
            // No IUserPreferencesProvider at all, which is the shape of "it cannot be read".
            Assert.Equal(OffLine, new LogLevelControl(container).Apply());
        }

        /// <summary>The preference survives a round trip through the on-disk model.</summary>
        [Fact]
        public void The_preference_is_written_and_read_back()
        {
            var prefs = new UserPreferences { DetailedLog = true };
            var file = prefs.ToFile();

            Assert.True(file.DetailedLog);
            Assert.True(UserPreferences.FromFile(file).DetailedLog);

            // A file from before this key existed reads as the default rather than as null.
            Assert.False(UserPreferences.FromFile(new UserPreferencesFile()).DetailedLog);
        }

        // --------------------------------------------- (b) the handler over a fake transport

        [Fact]
        public async Task A_request_writes_the_send_line_and_the_completion_line()
        {
            var logger = new Remembering();
            logger.Dial.MinimumLevel = LogEventLevel.Verbose;

            var inner = new Inner(_ => Ok(new string('x', 4242)));
            using var handler = new WireTraceHandler(inner, logger, new HttpTransportOptions());
            using var client = new HttpClient(handler, disposeHandler: false);

            using var answer = await client.GetAsync("https://thunderstore.io/c/valheim/api/v1/package-listing-index/");
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

            var lines = logger.Lines;
            Assert.Equal(2, lines.Count);

            Assert.StartsWith("HTTP GET https://thunderstore.io/c/valheim/api/v1/package-listing-index/ via ", lines[0]);
            // Both switches off, so nothing is said about addresses.
            Assert.DoesNotContain("IPv4 only", lines[0]);

            Assert.StartsWith("HTTP 200 https://thunderstore.io/c/valheim/api/v1/package-listing-index/ ", lines[1]);
            Assert.Contains("4,242 bytes", lines[1]);
            // "to headers", because a handler answers the moment the headers are in and the
            // body is read after it. This clock has never measured anything else.
            Assert.EndsWith(" ms to headers", lines[1]);
        }

        /// <summary>
        /// A body whose size the answer did not name is said to be of unknown size rather
        /// than of no size: zero bytes and "the server did not say" are different things.
        /// </summary>
        [Fact]
        public async Task A_body_with_no_declared_length_says_the_size_is_unknown()
        {
            var logger = new Remembering();
            logger.Dial.MinimumLevel = LogEventLevel.Verbose;

            var inner = new Inner(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new System.IO.MemoryStream(new byte[16])),
                };
                response.Content.Headers.ContentLength = null;
                return response;
            });

            using var handler = new WireTraceHandler(inner, logger, new HttpTransportOptions());
            using var client = new HttpClient(handler, disposeHandler: false);
            using var answer = await client.GetAsync("https://github.com/api/latest");

            Assert.Contains("size unknown", logger.Lines[1]);
        }

        /// <summary>The IPv4 switch is said on the send line, because it is part of how the request went out.</summary>
        [Fact]
        public async Task The_ipv4_switch_is_named_on_the_line_when_it_is_on()
        {
            var logger = new Remembering();
            logger.Dial.MinimumLevel = LogEventLevel.Verbose;

            var inner = new Inner(_ => Ok("hello"));
            using var handler = new WireTraceHandler(
                inner, logger, new HttpTransportOptions { IPv4Only = true, BypassProxy = true });
            using var client = new HttpClient(handler, disposeHandler: false);
            using var answer = await client.GetAsync("https://thunderstore.io/x");

            Assert.EndsWith(", IPv4 only", logger.Lines[0]);
            // And with the proxy switched off there is nothing to ask the machine about.
            Assert.Contains("direct (the Windows proxy is switched off)", logger.Lines[0]);
        }

        [Fact]
        public async Task A_request_that_threw_writes_the_failure_line_with_the_exception_type()
        {
            var logger = new Remembering();
            logger.Dial.MinimumLevel = LogEventLevel.Verbose;

            var inner = new Inner(_ => null);   // the Inner turns null into a throw
            using var handler = new WireTraceHandler(inner, logger, new HttpTransportOptions());
            using var client = new HttpClient(handler, disposeHandler: false);

            await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("https://thunderstore.io/x"));

            var lines = logger.Lines;
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("HTTP FAILED https://thunderstore.io/x after ", lines[1]);
            // The type it threw, and the message at the BOTTOM of it: an HttpRequestException's
            // own text is usually "An error occurred while sending the request" and the reason
            // is a socket failure two levels down, so the reason is what is said beside it.
            Assert.Contains("HttpRequestException: ", lines[1]);
            Assert.DoesNotContain("An error occurred while sending the request", lines[1]);
        }

        // ------------------------------------- (c) nothing from a header, nothing from a query

        /// <summary>
        /// This text is what a host pastes into a bug report. A header value can be a token
        /// and a query string can be a search term, an address or a name they did not choose
        /// to publish, so neither ever reaches the log.
        /// </summary>
        [Fact]
        public async Task Neither_a_header_value_nor_a_query_string_reaches_the_log()
        {
            var logger = new Remembering();
            logger.Dial.MinimumLevel = LogEventLevel.Verbose;

            var inner = new Inner(_ => Ok("ok"));
            using var handler = new WireTraceHandler(inner, logger, new HttpTransportOptions());
            using var client = new HttpClient(handler, disposeHandler: false);

            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://thunderstore.io/api/experimental/package/?q=SuperSecretModName&token=abc123");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer hunter2-do-not-log-me");
            request.Headers.TryAddWithoutValidation("X-Api-Key", "another-secret");

            using var answer = await client.SendAsync(request);

            var whole = string.Join("\n", logger.Lines);
            Assert.DoesNotContain("hunter2", whole);
            Assert.DoesNotContain("Bearer", whole);
            Assert.DoesNotContain("another-secret", whole);
            Assert.DoesNotContain("SuperSecretModName", whole);
            Assert.DoesNotContain("token=", whole);
            Assert.DoesNotContain("?", whole);

            // The path itself is there, because that is the half of the address that says
            // which read this was.
            Assert.Contains("https://thunderstore.io/api/experimental/package/", whole);
        }

        /// <summary>
        /// A Discord webhook keeps its key in the PATH, not in the query: the address the
        /// host pasted into the Discord card is https://discord.com/api/webhooks/{id}/{key},
        /// and anyone holding that key can post to the channel until it is rotated. Dropping
        /// the query is not enough for this one, and every server start, stop, join, leave
        /// and status edit goes through this handler.
        /// <para>
        /// The file this test protects is the one Troubleshooting tells a host to attach to a
        /// public issue, with Detailed log turned on first.
        /// </para>
        /// </summary>
        [Fact]
        public async Task A_webhook_key_never_reaches_the_log_although_it_lives_in_the_path()
        {
            var logger = new Remembering();
            logger.Dial.MinimumLevel = LogEventLevel.Verbose;

            var inner = new Inner(_ => Ok("{}"));
            using var handler = new WireTraceHandler(inner, logger, new HttpTransportOptions());
            using var client = new HttpClient(handler, disposeHandler: false);

            // The post the status service makes, and the edit it makes afterwards.
            using var post = await client.PostAsync(
                "https://discord.com/api/webhooks/1416962731234567890/XmS3cr3t-T0k3n_ThatGrantsPosting?wait=true",
                new StringContent("{}"));
            using var edit = await client.GetAsync(
                "https://discord.com/api/webhooks/1416962731234567890/XmS3cr3t-T0k3n_ThatGrantsPosting/messages/1417000000000000000");

            var whole = string.Join("\n", logger.Lines);
            Assert.DoesNotContain("XmS3cr3t-T0k3n_ThatGrantsPosting", whole);
            Assert.DoesNotContain("XmS3cr3t", whole);

            // What is left still says which read this was, which is the whole point of the line.
            Assert.Contains("https://discord.com/api/webhooks/1416962731234567890/", whole);
            Assert.Contains("(the webhook key)", whole);
            // And an edit is still told apart from a new post.
            Assert.Contains("/messages/1417000000000000000", whole);

            // A plain address is untouched: nothing here narrows an ordinary path.
            Assert.Equal(
                "https://thunderstore.io/api/experimental/package/",
                WireTrace.Address(new Uri("https://thunderstore.io/api/experimental/package/?q=x")));
        }

        // ------------------------------------------- (d) nothing at all with the dial at Debug

        /// <summary>
        /// The whole arrangement rests on this. With the log where every release has always
        /// had it, the handler writes nothing at all, so a host who has never heard of the
        /// switch has exactly the log they had before.
        /// </summary>
        [Fact]
        public async Task With_the_level_at_debug_the_handler_writes_nothing_at_all()
        {
            var logger = new Remembering();
            Assert.Equal(LogEventLevel.Debug, logger.Dial.MinimumLevel);

            var inner = new Inner(_ => Ok("hello"));
            using var handler = new WireTraceHandler(inner, logger, new HttpTransportOptions());
            using var client = new HttpClient(handler, disposeHandler: false);

            using var answer = await client.GetAsync("https://thunderstore.io/x");
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

            Assert.Empty(logger.Events);
            // And the request still went through: the trace standing aside must not stand in
            // the way.
            Assert.Single(inner.Seen);

            // A failure is silent at Debug too, and still throws to its caller.
            var failing = new Inner(_ => null);
            using var quiet = new WireTraceHandler(failing, logger, new HttpTransportOptions());
            using var second = new HttpClient(quiet, disposeHandler: false);
            await Assert.ThrowsAnyAsync<Exception>(() => second.GetAsync("https://thunderstore.io/y"));
            Assert.Empty(logger.Events);
        }

        /// <summary>
        /// And with no logger at all the provider hands out the bare shaped handler, which is
        /// what every test that is not about the trace has always been handed.
        /// </summary>
        [Fact]
        public void With_no_logger_there_is_no_trace_handler_in_the_way()
        {
            using var bare = HttpClientProvider.NewTracedHandler(HttpTransportOptions.Default, null);
            Assert.IsType<SocketsHttpHandler>(bare);

            var logger = new Remembering();
            using var traced = HttpClientProvider.NewTracedHandler(HttpTransportOptions.Default, logger);
            Assert.IsType<WireTraceHandler>(traced);
        }

        // ------------------------------------------------- the logger's own dial, end to end

        /// <summary>
        /// The dial has to sit in front of the WHOLE logger and not only in front of its file
        /// sink. The window's live stream and the in-memory tail are written by the pipeline
        /// itself, so a level check on the sink alone would have let every Verbose line
        /// through to both of them however the file was configured.
        /// </summary>
        [Fact]
        public void The_dial_stands_in_front_of_the_tail_and_the_live_stream_too()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IStartupArgsProvider>(new StartupArgsProvider(Array.Empty<string>()));
            services.AddSingleton<ILogLevelControl, LogLevelControl>();
            services.AddSingleton<IUserPreferencesProvider>(new MockUserPreferencesProvider());
            services.AddSingleton<ApplicationLogger>();
            using var container = services.BuildServiceProvider();

            var logger = container.GetRequiredService<ApplicationLogger>();
            // Through the interface, because that is how every caller in the app holds it and
            // it is Serilog's ILogger that carries Verbose() and Debug().
            ILogger writing = logger;
            var level = container.GetRequiredService<ILogLevelControl>();
            var streamed = new List<string>();
            logger.LogReceived += line => streamed.Add(line);

            // Put where a RELEASE stands, which is the level this is about: a DEBUG build's
            // dial starts open, and a dial that is open lets everything through by design.
            level.Apply(detailedLog: false);
            level.Switch.MinimumLevel = LogEventLevel.Debug;
            writing.Verbose("a line nobody asked for");
            Assert.Empty(streamed);
            Assert.DoesNotContain(logger.LogBuffer, l => l.Contains("nobody asked for"));
            Assert.False(logger.IsEnabled(LogEventLevel.Verbose));

            level.Apply(detailedLog: true);
            Assert.True(logger.IsEnabled(LogEventLevel.Verbose));
            writing.Verbose("a line that was asked for");
            Assert.Contains(streamed, l => l.Contains("a line that was asked for"));
            // And it is marked as the verbose line it is. The tag sits behind the timestamp,
            // which is the order the pipeline puts them in.
            Assert.Contains(streamed, l => l.Contains("[VER] ", StringComparison.Ordinal));

            // Debug still goes through either way, which is what a release has always written.
            level.Apply(detailedLog: false);
            level.Switch.MinimumLevel = LogEventLevel.Debug;
            writing.Debug("the ordinary sort of line");
            Assert.Contains(streamed, l => l.Contains("the ordinary sort of line"));
        }

        // ------------------------------------------------------ the app's OWN container

        /// <summary>
        /// The trace is wired in the container the app actually builds, not only in the one
        /// a test builds by hand.
        /// <para>
        /// Everything above proves the handler writes what it should once it has a logger.
        /// None of it proves the app ever gives it one: <c>HttpClientProvider</c> has three
        /// constructors, and if the container picked either of the shorter two then every
        /// client in the product would be handed a bare handler and Detailed log would be a
        /// switch that changed nothing. That is the shape of defect this file exists to
        /// catch, so it is asked of <see cref="Program.ConfigureServices"/> itself.
        /// </para>
        /// </summary>
        [Fact]
        public void The_apps_own_container_hands_the_provider_the_logger_to_trace_with()
        {
            var services = new ServiceCollection();
            Program.ConfigureServices(services, new[] { "--verbose" });
            using var container = services.BuildServiceProvider();

            var provider = Assert.IsType<HttpClientProvider>(container.GetRequiredService<IHttpClientProvider>());

            // The field rather than a behaviour, because a handler cannot be asked which of
            // the three constructors built the thing that made it.
            var field = typeof(HttpClientProvider)
                .GetField("Tracer", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);

            // The control, so this is not an assertion that passes whatever the container
            // did: built through the shorter constructor the very same field is null.
            using (var shorter = new HttpClientProvider((IHttpTransportSettings)null))
            {
                Assert.Null(field.GetValue(shorter));
            }

            var tracer = field.GetValue(provider);

            Assert.NotNull(tracer);
            Assert.Same(container.GetRequiredService<ILogger>(), tracer);

            // And the same container reads the command line, so a host who was told to start
            // the exe with --verbose gets Verbose and is told which of the two set it.
            var level = container.GetRequiredService<ILogLevelControl>();
            Assert.True(level.ForcedByCommandLine);
            Assert.Equal("Log level Verbose (--verbose)", level.Apply());
            Assert.Equal(LogEventLevel.Verbose, level.Current);
        }
    }
}
