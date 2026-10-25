using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using Serilog.Parsing;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The day on the log wire (1.3.0, H1 item 1). A line carries only its time, and a log read
    /// on screen across midnight could not say which day a line was written on. Each held line
    /// now keeps the host's own date it was stamped on, read off the same moment the time is
    /// printed from, and every row and event the page reads carries it as "day". The text of
    /// the line, and so the files on disk, do not change.
    /// </summary>
    public class LogDayTests
    {
        /// <summary>A pipeline logger with no file and only the time stamp, as the app and server logs stamp lines.</summary>
        private sealed class Stamped : PipelineLogger, IApplicationLogger
        {
            public Stamped() => Use(LogSteps.Timestamp());

            protected override string ResolveLogFile() => null;
        }

        private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

        private static LogEvent At(DateTimeOffset when, string text) =>
            new(when, LogEventLevel.Information, null, new MessageTemplateParser().Parse(text), Array.Empty<LogEventProperty>());

        [Fact]
        public void A_line_stamped_before_midnight_and_one_after_carry_different_days()
        {
            var log = new Stamped();
            log.Write(At(new DateTimeOffset(2026, 10, 24, 23, 59, 59, 999, Offset), "the last of the day"));
            log.Write(At(new DateTimeOffset(2026, 10, 25, 0, 0, 0, 1, Offset), "the first of the next"));

            var held = log.TaggedBuffer.ToList();
            Assert.Equal(2, held.Count);
            Assert.Equal("[23:59:59.999] the last of the day", held[0].Line);
            Assert.Equal("2026-10-24", held[0].Day);
            Assert.Equal("[00:00:00.001] the first of the next", held[1].Line);
            Assert.Equal("2026-10-25", held[1].Day);

            // The plain tail the files and every other reader see is the line alone, as before.
            Assert.Equal(new[] { "[23:59:59.999] the last of the day", "[00:00:00.001] the first of the next" }, log.LogBuffer.ToArray());
        }

        [Fact]
        public void The_day_is_the_date_the_time_was_printed_from_whatever_the_culture()
        {
            var before = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                // A culture whose own calendar and digits would print another date.
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
                Assert.Equal("2026-01-02", LogSteps.DayOf(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = before;
            }

            // The offset the moment was taken in is the one the date is read in: 01:00 at +02:00
            // is still the evening before in UTC, and the host reads its own clock.
            Assert.Equal("2026-03-01", LogSteps.DayOf(new DateTimeOffset(2026, 3, 1, 1, 0, 0, TimeSpan.FromHours(2))));
        }

        [Fact]
        public void The_app_buffer_rows_carry_line_profile_and_day()
        {
            var log = new Stamped();
            using (AppLog.ForRealm("Alpha")) log.Write(At(new DateTimeOffset(2026, 10, 24, 23, 59, 0, Offset), "alpha line"));
            log.Write(At(new DateTimeOffset(2026, 10, 25, 0, 1, 0, Offset), "no realm"));

            var rows = JArray.Parse(JsonConvert.SerializeObject(BlendWindow.AppLogBufferRows(log)));

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "day", "line", "profile" }, ((JObject)rows[0]).Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal("[23:59:00.000] alpha line", rows[0].Value<string>("line"));
            Assert.Equal("Alpha", rows[0].Value<string>("profile"));
            Assert.Equal("2026-10-24", rows[0].Value<string>("day"));
            Assert.Equal(JTokenType.Null, rows[1]["profile"]!.Type);
            Assert.Equal("2026-10-25", rows[1].Value<string>("day"));
        }

        [Fact]
        public void A_logger_that_keeps_no_tags_gives_rows_with_no_day_rather_than_a_made_up_one()
        {
            var plain = new PlainTail("[10:00:00.000] a line");
            var rows = JArray.Parse(JsonConvert.SerializeObject(BlendWindow.AppLogBufferRows(plain)));
            Assert.Equal(JTokenType.Null, Assert.Single(rows)["day"]!.Type);

            var server = JArray.Parse(JsonConvert.SerializeObject(BlendWindow.ServerBufferRows(plain, "Alpha")));
            Assert.Equal("Alpha", Assert.Single(server).Value<string>("profile"));
            Assert.Equal(JTokenType.Null, server[0]["day"]!.Type);
        }

        [Fact]
        public void The_server_log_keeps_the_day_and_its_replay_and_live_event_carry_it()
        {
            var options = new ValheimServerOptions { Name = "Alpha", LogToFile = false };
            // Nothing asked for the live view yet: an options object names no line handler.
            Assert.Null(options.LogLineHandler);
            var log = new ValheimServerLogger(options);
            var heard = new List<AppLogLine>();
            ((IRealmTaggedLog)log).LineReceived += heard.Add;

            log.Write(At(new DateTimeOffset(2026, 10, 24, 23, 59, 59, 500, Offset), "Game server connected"));
            log.Write(At(new DateTimeOffset(2026, 10, 25, 0, 0, 0, 500, Offset), "Got connection SteamID 1"));

            var replay = JArray.Parse(JsonConvert.SerializeObject(BlendWindow.ServerBufferRows(log, "Alpha")));
            Assert.Equal(new[] { "2026-10-24", "2026-10-25" }, replay.Select(r => r.Value<string>("day")).ToArray());
            Assert.All(replay, r => Assert.Equal("Alpha", r.Value<string>("profile")));

            // The live event is the same three fields, the day read off the held line.
            Assert.Equal(2, heard.Count);
            var live = JObject.FromObject(BlendWindow.LogLinePayload(heard[1], "Alpha"));
            Assert.Equal(new[] { "day", "line", "profile" }, live.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal("[00:00:00.500] Got connection SteamID 1", live.Value<string>("line"));
            Assert.Equal("Alpha", live.Value<string>("profile"));
            Assert.Equal("2026-10-25", live.Value<string>("day"));
        }

        private sealed class PlainTail : IBufferedLogger
        {
            private readonly string[] Lines;

            public PlainTail(params string[] lines) => Lines = lines;

            public event Action<string> LogReceived { add { } remove { } }

            public IEnumerable<string> LogBuffer => Lines;

            public void Write(LogEvent logEvent) { }
        }
    }
}
