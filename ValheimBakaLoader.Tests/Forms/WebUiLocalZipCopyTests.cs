using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Every refusal a zip on this PC can answer with (1.3.2, issue 32) reaches the host in their
    /// own language: each "local.*" reason the reader or the bridge sends has a LOCAL_ZIP_REASONS row,
    /// that row's catalog id exists, and every {name} its sentence needs is a value the host sends
    /// with that reason. A reason added later without its row, or a placeholder renamed on one side
    /// only, fails here instead of showing the host an English fallback or a raw {brace}.
    /// </summary>
    public class WebUiLocalZipCopyTests
    {
        private static readonly Regex Literal = new("\"(local\\.[A-Za-z]+)\"", RegexOptions.Compiled);
        private static readonly Regex RefuseCall = new("Refuse\\(\"(local\\.[A-Za-z]+)\",(?<rest>.*?)(?=\\);|Refuse\\()", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex TupleKey = new("\\(\"(\\w+)\",", RegexOptions.Compiled);
        private static readonly Regex Placeholder = new("\\{(\\w+)\\}", RegexOptions.Compiled);

        private static string Reader() => AppSourceTree.Read("ValheimBakaLoader", "Tools", "LocalPackageReader.cs");
        private static string Bridge() => AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

        /// <summary>The reasons, each with the value names the host sends beside it.</summary>
        private static Dictionary<string, HashSet<string>> Sent()
        {
            var sent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            void Add(string reason, IEnumerable<string> keys)
            {
                if (!sent.TryGetValue(reason, out var set)) sent[reason] = set = new HashSet<string>(StringComparer.Ordinal);
                set.UnionWith(keys);
            }

            foreach (Match m in RefuseCall.Matches(Reader()))
                Add(m.Groups[1].Value, TupleKey.Matches(m.Groups["rest"].Value).Select(t => t.Groups[1].Value));
            foreach (Match m in Literal.Matches(Reader())) Add(m.Groups[1].Value, Array.Empty<string>());

            // The bridge's own two: the path rule's problems (sent as "local." + problem, with the
            // path) and the running server (with its name).
            foreach (var problem in new[] { LocalPackagePath.NotAbsolute, LocalPackagePath.UnbalancedQuote })
                Add("local." + problem, new[] { "path" });
            Add("local.serverRunning", new[] { "name" });
            foreach (Match m in Literal.Matches(Bridge())) Add(m.Groups[1].Value, Array.Empty<string>());
            return sent;
        }

        private static Dictionary<string, string> Rows()
        {
            var js = AppSourceTree.Web("app.js");
            var start = js.IndexOf("const LOCAL_ZIP_REASONS=[", StringComparison.Ordinal);
            Assert.True(start >= 0, "app.js has no LOCAL_ZIP_REASONS table");
            var table = js.Substring(start, js.IndexOf("];", start, StringComparison.Ordinal) - start);
            return Regex.Matches(table, "\\{named:\"(local\\.[A-Za-z]+)\",\\s*textId:\"([\\w.]+)\"\\}")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        }

        private static string Lore(JsonElement keys, string id)
        {
            Assert.True(keys.TryGetProperty(id, out var entry), "the English catalog has no " + id);
            return entry.GetProperty("lore").GetString();
        }

        [Fact]
        public void Every_local_reason_has_a_row_and_its_sentence_names_only_what_is_sent()
        {
            var sent = Sent();
            var rows = Rows();
            Assert.True(sent.Count >= 23, "the reason scan found only " + sent.Count);

            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var keys = document.RootElement.GetProperty("keys");

            foreach (var (reason, values) in sent)
            {
                Assert.True(rows.TryGetValue(reason, out var id), reason + " has no LOCAL_ZIP_REASONS row, so the host would read the English fallback");
                foreach (Match p in Placeholder.Matches(Lore(keys, id)))
                    Assert.True(values.Contains(p.Groups[1].Value), $"{id} needs {{{p.Groups[1].Value}}}, which {reason} does not send");
            }

            // And no row for a reason nothing sends.
            Assert.Empty(rows.Keys.Where(r => !sent.ContainsKey(r)));
        }

        [Fact]
        public void The_reason_scan_sees_a_reason_with_no_row()
        {
            // The gate's own falsification: a reason missing from the table is caught.
            var rows = Rows();
            rows.Remove("local.locked");
            Assert.Contains(Sent().Keys, r => !rows.ContainsKey(r));
        }
    }
}
