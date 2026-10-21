using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// With two realms running, the one application log interleaved both realms' lines with
    /// nothing to tell them apart, and each realm's Saga read the other's starts, stops and
    /// sweeps. These hold the host half of the fix: a line carries the realm it was written
    /// for, from a scope or a tagged logger, the ring keeps it, and the replay hands it over.
    /// </summary>
    public class AppLogRealmTests : BaseTest, IDisposable
    {
        // A record book of its own: one test here builds a server, and a server start's install
        // pass writes the companion plugin record. See
        // CompanionPluginStatusTests.Every_test_class_that_touches_the_record_keeps_a_book_of_its_own.
        private readonly IDisposable OwnRecords = CompanionPluginStatus.BeginOwnRecords();

        public void Dispose() => OwnRecords.Dispose();

        /// <summary>A pipeline logger with no file and no steps, so a test reads the bare line.</summary>
        private sealed class Bare : PipelineLogger, IApplicationLogger
        {
            protected override string ResolveLogFile() => null;
        }

        private static string RealmOf(Bare log, string text)
            => log.TaggedBuffer.Last(l => l.Line.Contains(text, StringComparison.Ordinal)).Profile;

        [Fact]
        public void A_scope_tags_the_line_and_a_line_outside_one_belongs_to_no_realm()
        {
            var log = new Bare();
            ILogger writing = log;

            writing.Information("before any realm");
            using (AppLog.ForRealm("Alpha")) writing.Information("inside Alpha");
            writing.Information("after Alpha");

            Assert.Null(RealmOf(log, "before any realm"));
            Assert.Equal("Alpha", RealmOf(log, "inside Alpha"));
            Assert.Null(RealmOf(log, "after Alpha"));
        }

        [Fact]
        public void Scopes_nest_and_each_one_puts_back_what_was_there()
        {
            var log = new Bare();
            ILogger writing = log;

            using (AppLog.ForRealm("Alpha"))
            {
                using (AppLog.ForRealm("Beta")) writing.Information("inner Beta");
                writing.Information("outer Alpha again");

                // A path that lost its realm name is not a claim that it belongs to none.
                using (AppLog.ForRealm("  ")) writing.Information("blank keeps Alpha");

                using (AppLog.ForNoRealm()) writing.Information("said to be no realm");
            }

            Assert.Equal("Beta", RealmOf(log, "inner Beta"));
            Assert.Equal("Alpha", RealmOf(log, "outer Alpha again"));
            Assert.Equal("Alpha", RealmOf(log, "blank keeps Alpha"));
            Assert.Null(RealmOf(log, "said to be no realm"));
            Assert.Null(AppLog.CurrentRealm);
        }

        [Fact]
        public async Task The_scope_follows_the_work_through_awaits_and_into_its_tasks_and_nowhere_else()
        {
            var log = new Bare();
            ILogger writing = log;

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var elsewhere = Task.Run(async () =>
            {
                await gate.Task;
                writing.Information("started outside the scope");
            });

            using (AppLog.ForRealm("Alpha"))
            {
                await Task.Yield();
                writing.Information("after an await");
                await Task.Run(() => writing.Information("in a task it started"));
                gate.SetResult(true);
                await elsewhere;
            }

            Assert.Equal("Alpha", RealmOf(log, "after an await"));
            Assert.Equal("Alpha", RealmOf(log, "in a task it started"));
            Assert.Null(RealmOf(log, "started outside the scope"));
        }

        [Fact]
        public void The_ring_keeps_the_tag_and_the_plain_tail_and_events_are_unchanged()
        {
            var log = new Bare();
            ILogger writing = log;
            var plain = new List<string>();
            var tagged = new List<AppLogLine>();
            log.LogReceived += plain.Add;
            log.LineReceived += tagged.Add;

            using (AppLog.ForRealm("Alpha")) writing.Warning("alpha {what}", "warned");
            writing.Information("no realm");

            Assert.Equal(new[] { "alpha warned", "no realm" }, log.LogBuffer);
            Assert.Equal(log.LogBuffer, log.TaggedBuffer.Select(l => l.Line));
            Assert.Equal(plain, tagged.Select(l => l.Line));
            Assert.Equal(new[] { "Alpha", null }, tagged.Select(l => l.Profile));
            Assert.DoesNotContain("Alpha", log.TaggedBuffer.First().Line, StringComparison.Ordinal);
        }

        [Fact]
        public void A_tagged_logger_names_its_realm_per_line_and_writes_through_the_real_one()
        {
            var log = new Bare();
            var realm = "Alpha";
            var server = ((IApplicationLogger)log).ForRealm(() => realm);
            var fixedOne = ((IApplicationLogger)log).ForRealm("Beta");
            var heard = new List<string>();
            server.LogReceived += heard.Add;

            server.Information("first {n}", 1);
            realm = "Gamma";
            server.Information("second {n}", 2);
            fixedOne.Error("beta's own");

            Assert.Equal("Alpha", RealmOf(log, "first 1"));
            Assert.Equal("Gamma", RealmOf(log, "second 2"));
            Assert.Equal("Beta", RealmOf(log, "beta's own"));
            Assert.Contains("first 1", heard);
            Assert.Equal(3, log.LogBuffer.Count());
            Assert.Null(AppLog.CurrentRealm);
        }

        [Fact]
        public void A_rename_takes_the_held_lines_to_the_new_name()
        {
            var log = new Bare();
            ILogger writing = log;
            using (AppLog.ForRealm("Alpha")) writing.Information("alpha line");
            using (AppLog.ForRealm("Beta")) writing.Information("beta line");

            log.RenameRealm("ALPHA", "Hearthstone");

            Assert.Equal("Hearthstone", RealmOf(log, "alpha line"));
            Assert.Equal("Beta", RealmOf(log, "beta line"));
        }

        [Fact]
        public void The_app_buffer_replay_is_rows_of_line_and_profile()
        {
            var log = new Bare();
            ILogger writing = log;
            using (AppLog.ForRealm("Alpha")) writing.Information("alpha line");
            writing.Information("no realm");

            var json = JArray.Parse(JsonConvert.SerializeObject(BlendWindow.AppLogBufferRows(log)));

            Assert.Equal(2, json.Count);
            Assert.Equal("alpha line", json[0].Value<string>("line"));
            Assert.Equal("Alpha", json[0].Value<string>("profile"));
            Assert.Equal("no realm", json[1].Value<string>("line"));
            Assert.Equal(JTokenType.Null, json[1]["profile"].Type);
        }

        [Fact]
        public void A_command_belongs_to_the_realm_it_names_else_the_one_on_screen_for_realm_work()
        {
            var named = JObject.Parse("{\"profile\":\"Beta\"}");
            var prefs = JObject.Parse("{\"prefs\":{\"ProfileName\":\"Beta\"}}");
            var none = new JObject();

            Assert.Equal("Beta", BlendWindow.RealmOfCall("server.update", named, "Alpha"));
            Assert.Equal("Beta", BlendWindow.RealmOfCall("server.start", prefs, "Alpha"));
            Assert.Equal("Alpha", BlendWindow.RealmOfCall("server.stop", none, "Alpha"));
            Assert.Equal("Alpha", BlendWindow.RealmOfCall("players.cleanse", none, "Alpha"));
            Assert.Equal("Alpha", BlendWindow.RealmOfCall("mods.update", none, "Alpha"));

            // App settings, language and paths are nobody's realm.
            Assert.Null(BlendWindow.RealmOfCall("userprefs.save", none, "Alpha"));
            Assert.Null(BlendWindow.RealmOfCall("lang.download", none, "Alpha"));

            // A realm that is going away cannot be on screen afterwards, so its lines stay readable.
            Assert.Null(BlendWindow.RealmOfCall("profiles.delete", named, "Alpha"));
            Assert.Null(BlendWindow.RealmOfCall("profiles.rename", JObject.Parse("{\"name\":\"Alpha\"}"), "Alpha"));
        }

        [Fact]
        public void A_server_writes_its_application_lines_as_its_own_realm()
        {
            var server = GetService<ValheimServer>();
            var appLog = GetService<IApplicationLogger>();
            Assert.IsAssignableFrom<IRealmTaggedLog>(appLog);

            server.ServerKey = "Alpha";
            var held = typeof(ValheimServer)
                .GetField("ApplicationLogger", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(server) as IApplicationLogger;
            Assert.NotNull(held);

            var heard = new List<AppLogLine>();
            void Hear(AppLogLine l) => heard.Add(l);
            ((IRealmTaggedLog)appLog).LineReceived += Hear;
            try
            {
                held.Information("a line from the server {n}", 7);
                appLog.Information("a line from the app");
            }
            finally
            {
                ((IRealmTaggedLog)appLog).LineReceived -= Hear;
            }

            Assert.Equal("Alpha", heard.Single(l => l.Line.Contains("a line from the server 7", StringComparison.Ordinal)).Profile);
            Assert.Null(heard.Single(l => l.Line.Contains("a line from the app", StringComparison.Ordinal)).Profile);
        }
    }
}
