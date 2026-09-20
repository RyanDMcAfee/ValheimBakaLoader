using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The gate that catches the NEXT raw English sentence on its way out of the app.
    /// <para>
    /// HostSentenceCopyTests pins the sentences that were moved into the catalog, one by one,
    /// by name. That is a list of what happened and it cannot see what happens next: a line
    /// added tomorrow, handing a new English sentence to a broadcast or to a Discord post, is
    /// a sentence nobody wrote a DoesNotContain for. The copy gate cannot see it either. It
    /// counts dashes, and it says so at the top of itself, which is exactly how the countdown
    /// chip's English shipped under a green gate once already.
    /// </para>
    /// <para>
    /// So this asks the question from the other end. There are three roads out of this app
    /// that end at a person who is not the host: the RCON broadcast a player reads in the
    /// middle of their screen, the kick a player is thrown off the server by, and everything
    /// posted to Discord. For each of them, what arrives at the road is followed back to what
    /// wrote it, and any English sentence found on the way that did not come out of
    /// <c>HostCatalog.T</c> is a finding.
    /// </para>
    /// <para>
    /// The allowlist is small on purpose and every entry says what it is. A wire token is not
    /// copy; a log line never reaches a player and is never scanned, because none of these
    /// roads is a logger.
    /// </para>
    /// </summary>
    public class HostRouteEnglishGateTests
    {
        // ------------------------------------------------------------------ the roads

        /// <summary>
        /// Where a sentence is handed over: the file, and the call that takes it. The
        /// definition of the method matches too, which costs nothing (a parameter list holds
        /// no literals) and means a renamed route cannot quietly drop off this list.
        /// </summary>
        private static readonly (string File, string Call)[] HandOvers =
        {
            // The RCON broadcast, both roads to it: the countdown's own and the free one the
            // host types into the Saga terminal.
            ("ValheimServer.cs", @"SendCountdownBroadcastAsync\s*\("),
            ("ValheimServer.cs", @"BroadcastNow\s*\("),
            ("BlendWindow.Bridge.cs", @"BroadcastNow\s*\("),

            // The kick.
            ("ValheimServer.cs", @"KickAsync\s*\("),
            ("BlendWindow.Bridge.cs", @"KickAsync\s*\("),

            // Discord: the nine event posts, at the call and at the embed they build.
            ("BlendWindow.Bridge.cs", @"DiscordWebhooks\.Send[A-Za-z]*\s*\("),
            ("DiscordWebhookService.cs", @"SendEmbed\s*\("),
        };

        /// <summary>
        /// Where the text that goes on the wire is actually assembled. The whole body is read,
        /// because a sentence added here would never appear at a call site at all.
        /// </summary>
        private static readonly (string File, string Method)[] Assemblers =
        {
            ("ValheimServer.cs", "BuildBroadcast"),
            ("ValheimServer.cs", "BuildKick"),
            ("DiscordStatusService.cs", "BuildEmbedPayload"),
        };

        /// <summary>
        /// The literals that are not copy. One line each, and each one says why.
        /// </summary>
        private static readonly string[] NotCopy =
        {
            // The RCON command itself and where on the screen it puts the message. Two words
            // of wire protocol read by the Server devcommands mod, never by a person.
            "broadcast center ",
        };

        // ------------------------------------------------------------------ the gate

        /// <summary>
        /// An English sentence, as far as this gate is concerned: two letters with a single
        /// space between them. It is a blunt rule and it is the right blunt rule here. Every
        /// sentence these roads ever carried matches it, and the things that legitimately go
        /// out in English do not: a command token, a Discord timestamp, a bare hyphen, a
        /// backtick, a name the host typed.
        /// </summary>
        internal static bool ReadsAsProse(string literal) =>
            literal != null &&
            Regex.IsMatch(literal, "[A-Za-z] [A-Za-z]") &&
            !NotCopy.Any(allowed => literal.Contains(allowed, StringComparison.Ordinal));

        /// <summary>
        /// Every English sentence handed to a road in this source, with nothing that came out
        /// of the catalog and nothing that was only ever a comment.
        /// </summary>
        internal static List<string> RawEnglish(string source, string callPattern)
        {
            var clean = WithoutComments(source);
            var found = new List<string>();

            foreach (Match call in Regex.Matches(clean, callPattern))
            {
                var open = clean.IndexOf('(', call.Index);
                if (open < 0) continue;

                var arguments = Balanced(clean, open, '(', ')');
                if (arguments == null) continue;

                foreach (var literal in Reachable(clean, arguments, call.Index))
                {
                    if (ReadsAsProse(literal)) found.Add(literal);
                }
            }

            return found;
        }

        /// <summary>The same question asked of a whole method body.</summary>
        internal static List<string> RawEnglishInBody(string source, string method)
        {
            var clean = WithoutComments(source);
            var body = MethodBody(clean, method, out var at);

            return body == null
                ? new List<string>()
                : Reachable(clean, body, at).Where(ReadsAsProse).ToList();
        }

        /// <summary>
        /// Every literal an expression can reach: the ones written into it, and the ones
        /// written into a local it names. One hop is enough, and it is the hop that matters:
        /// assigning a sentence to a variable and handing the variable over is the obvious way
        /// round a gate that only reads call sites.
        /// </summary>
        private static List<string> Reachable(string source, string expression, int at)
        {
            var found = new List<string>(Literals(WithoutCatalogCalls(expression)));

            foreach (Match name in Regex.Matches(expression, @"(?<![A-Za-z0-9_.])[a-z][A-Za-z0-9]*"))
            {
                var assigned = LocalAssignment(source, name.Value, at);
                if (assigned != null) found.AddRange(Literals(WithoutCatalogCalls(assigned)));
            }

            return found;
        }

        /// <summary>
        /// What a local is given when it is declared, or null when it is not one.
        /// <para>
        /// The nearest declaration ABOVE the hand-over, and only one within arm's reach of it.
        /// A name like <c>name</c> or <c>message</c> is declared a dozen times in a file this
        /// size, and the first one in the file is almost never the one being handed over; the
        /// one a few lines up almost always is.
        /// </para>
        /// </summary>
        private static string LocalAssignment(string source, string name, int at)
        {
            const int reach = 3000;

            Match declaration = null;
            foreach (Match candidate in Regex.Matches(
                source, @"(?<![A-Za-z0-9_])(?:var|string)\s+" + Regex.Escape(name) + @"\s*="))
            {
                if (candidate.Index > at) break;
                if (at - candidate.Index <= reach) declaration = candidate;
            }

            if (declaration == null) return null;

            var from = declaration.Index + declaration.Length;
            var depth = 0;

            for (var i = from; i < source.Length; i++)
            {
                if (IsLiteralStart(source, i, out var end, out _)) { i = end; continue; }

                var c = source[i];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == ';' && depth <= 0) return source.Substring(from, i - from);
            }

            return null;
        }

        // ------------------------------------------------------------------ reading C# as text

        /// <summary>
        /// The source with every comment replaced by blanks, the length unchanged. A sentence
        /// explaining a sentence is not a sentence anybody reads on a server.
        /// </summary>
        internal static string WithoutComments(string source)
        {
            var kept = new StringBuilder(source);

            for (var i = 0; i < source.Length; i++)
            {
                if (IsLiteralStart(source, i, out var end, out _)) { i = end; continue; }

                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n') kept[i++] = ' ';
                    continue;
                }

                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    var stop = close < 0 ? source.Length : close + 2;
                    for (; i < stop; i++) if (source[i] != '\n') kept[i] = ' ';
                    i--;
                }
            }

            return kept.ToString();
        }

        /// <summary>
        /// Whether a literal starts here, where it ends, and what is in it with every
        /// interpolation hole dropped: a hole is an expression, and an expression is not copy.
        /// </summary>
        internal static bool IsLiteralStart(string source, int at, out int end, out string text)
        {
            end = at;
            text = null;

            var i = at;
            var interpolated = false;
            var verbatim = false;

            while (i < source.Length && (source[i] == '$' || source[i] == '@'))
            {
                interpolated |= source[i] == '$';
                verbatim |= source[i] == '@';
                i++;
            }

            if (i >= source.Length || source[i] != '"') return false;

            // A quote with a letter or a digit directly in front of it is not the start of
            // anything; it is the end of something this walk already stepped over.
            if (at > 0 && (char.IsLetterOrDigit(source[at - 1]) || source[at - 1] == '_')) return false;

            var inside = new StringBuilder();
            var depth = 0;
            i++;

            for (; i < source.Length; i++)
            {
                var c = source[i];

                if (verbatim && c == '"')
                {
                    if (i + 1 < source.Length && source[i + 1] == '"') { inside.Append('"'); i++; continue; }
                    break;
                }

                if (!verbatim && c == '\\') { i++; continue; }

                if (interpolated && c == '{')
                {
                    if (i + 1 < source.Length && source[i + 1] == '{') { i++; continue; }
                    depth++;
                    continue;
                }

                if (interpolated && c == '}')
                {
                    if (depth > 0) depth--;
                    else if (i + 1 < source.Length && source[i + 1] == '}') i++;
                    continue;
                }

                if (depth > 0) continue;
                if (!verbatim && c == '"') break;
                if (c == '"') break;

                inside.Append(c);
            }

            end = Math.Min(i, source.Length - 1);
            text = inside.ToString();
            return true;
        }

        /// <summary>Every literal in a span of C#, holes already dropped.</summary>
        internal static List<string> Literals(string span)
        {
            var found = new List<string>();

            for (var i = 0; i < span.Length; i++)
            {
                if (!IsLiteralStart(span, i, out var end, out var text)) continue;

                found.Add(text);
                i = end;
            }

            return found;
        }

        /// <summary>
        /// The span with every <c>HostCatalog.T(...)</c> taken out of it. What is left is what
        /// did not come from the catalog, which is the whole question this gate asks.
        /// </summary>
        internal static string WithoutCatalogCalls(string span)
        {
            const string call = "HostCatalog.T(";

            while (true)
            {
                var at = span.IndexOf(call, StringComparison.Ordinal);
                if (at < 0) return span;

                var open = at + call.Length - 1;
                var inner = Balanced(span, open, '(', ')');
                if (inner == null) return span.Substring(0, at);

                span = span.Substring(0, at) + " " + span.Substring(open + inner.Length + 2);
            }
        }

        /// <summary>What sits between an opener and the one that closes it, or null.</summary>
        private static string Balanced(string source, int open, char opener, char closer)
        {
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (i != open && IsLiteralStart(source, i, out var end, out _)) { i = end; continue; }

                if (source[i] == opener) depth++;
                else if (source[i] == closer)
                {
                    depth--;
                    if (depth == 0) return source.Substring(open + 1, i - open - 1);
                }
            }

            return null;
        }

        /// <summary>
        /// A method's body, braces excluded, or null when there is no such method. The
        /// declaration is told from a call to the same name by what follows the closing
        /// bracket: a body opens a brace, and a call does not. Every one of these three is
        /// called above the line that declares it, so taking the first match would read the
        /// wrong block entirely.
        /// </summary>
        internal static string MethodBody(string source, string method, out int at)
        {
            at = -1;

            foreach (Match signature in Regex.Matches(
                source, @"(?<![A-Za-z0-9_])" + Regex.Escape(method) + @"\s*\("))
            {
                var open = source.IndexOf('(', signature.Index);
                var arguments = Balanced(source, open, '(', ')');
                if (arguments == null) continue;

                var after = open + arguments.Length + 2;
                while (after < source.Length && char.IsWhiteSpace(source[after])) after++;
                if (after >= source.Length || source[after] != '{') continue;

                at = after;
                return Balanced(source, after, '{', '}');
            }

            return null;
        }

        // ------------------------------------------------------------------ the live tree

        [Fact]
        public void No_english_sentence_is_handed_to_a_broadcast_a_kick_or_a_discord_post()
        {
            var files = AppSourceTree.Files();
            var findings = new List<string>();

            foreach (var (file, call) in HandOvers)
            {
                foreach (var sentence in RawEnglish(files[file], call))
                    findings.Add(file + ": \"" + sentence + "\"");
            }

            foreach (var (file, method) in Assemblers)
            {
                foreach (var sentence in RawEnglishInBody(files[file], method))
                    findings.Add(file + " " + method + ": \"" + sentence + "\"");
            }

            Assert.True(findings.Count == 0,
                "these sentences reach a player without going through HostCatalog.T:\n  " +
                string.Join("\n  ", findings));
        }

        /// <summary>
        /// Every road is actually watched. A list that quietly lost an entry would pass the
        /// check above while watching nothing, which is the failure mode of every gate written
        /// as a list of places.
        /// </summary>
        [Fact]
        public void All_three_roads_are_on_the_list()
        {
            var watched = HandOvers.Select(h => h.Call)
                .Concat(Assemblers.Select(a => a.Method))
                .ToList();

            Assert.Contains(watched, w => w.Contains("Broadcast", StringComparison.Ordinal));
            Assert.Contains(watched, w => w.Contains("Kick", StringComparison.Ordinal));
            Assert.Contains(watched, w => w.Contains("Discord", StringComparison.Ordinal) ||
                                          w.Contains("SendEmbed", StringComparison.Ordinal));

            // And every road still goes somewhere. A gate written as a list of call names goes
            // green the day one of them is renamed, having scanned nothing at all, which is
            // the way this kind of gate always dies. So every pattern has to match a real call
            // in a real file, and every assembler has to have a body.
            var files = AppSourceTree.Files();
            foreach (var name in HandOvers.Select(h => h.File).Concat(Assemblers.Select(a => a.File)).Distinct())
                Assert.True(files.ContainsKey(name), name + " is not in the app source tree any more");

            foreach (var (file, call) in HandOvers)
            {
                Assert.True(
                    Regex.IsMatch(WithoutComments(files[file]), call),
                    call + " matches nothing in " + file + " any more, so that road is unwatched");
            }

            foreach (var (file, method) in Assemblers)
            {
                Assert.True(
                    MethodBody(WithoutComments(files[file]), method, out _) != null,
                    method + " has no body in " + file + " any more, so that road is unwatched");
            }
        }

        // ------------------------------------------------------------------ the gate fires

        /// <summary>
        /// The shape this gate exists for, written the way it was actually written before the
        /// catalog: the countdown's own broadcast, and a Discord post with its title and body
        /// spelled out. A gate that has never been shown to fail is a gate nobody has tested.
        /// </summary>
        [Fact]
        public void The_gate_fails_on_the_english_that_used_to_be_here()
        {
            const string before = @"
                private async Task RunCountdown()
                {
                    await SendCountdownBroadcastAsync($""Server restarting in {FormatTime(remaining)}!{updateNote}"");
                    await SendCountdownBroadcastAsync(""Server restarting NOW!"");
                }";

            var caught = RawEnglish(before, @"SendCountdownBroadcastAsync\s*\(");

            // The interpolated one reads as its literal halves with the hole dropped, which is
            // the whole of what the gate has an opinion about: an expression is not copy and
            // "Server restarting in " is.
            Assert.Equal(2, caught.Count);
            Assert.Contains("Server restarting in !", caught);
            Assert.Contains("Server restarting NOW!", caught);

            const string post = @"
                public void SendServerStarted(string serverName)
                {
                    SendEmbed(""Server Started"", $""**{serverName}** is now online."", 0x57F287);
                }";

            Assert.Equal(2, RawEnglish(post, @"SendEmbed\s*\(").Count);
        }

        /// <summary>
        /// And the way round it that a call-site-only gate would miss: park the sentence in a
        /// local first, hand the local over.
        /// </summary>
        [Fact]
        public void The_gate_follows_a_sentence_parked_in_a_local_first()
        {
            const string parked = @"
                private async Task RunCountdown()
                {
                    var notice = ""The server is going down for maintenance."";
                    await SendCountdownBroadcastAsync(notice);
                }";

            Assert.Contains("The server is going down for maintenance.",
                RawEnglish(parked, @"SendCountdownBroadcastAsync\s*\("));
        }

        /// <summary>
        /// And it lets through what legitimately stays English. A sentence that came out of the
        /// catalog is not a finding, a wire token is not a finding, and neither is a comment
        /// with a sentence in it.
        /// </summary>
        [Fact]
        public void The_gate_lets_the_catalog_and_the_wire_tokens_through()
        {
            const string after = @"
                private async Task RunCountdown()
                {
                    // Used to say Server restarting in five minutes right here.
                    var updateNote = pending > 0 ? "" "" + HostCatalog.T(""host.countdown.update_note"", (""count"", pending)) : string.Empty;
                    await SendCountdownBroadcastAsync(
                        HostCatalog.T(""host.countdown.restart_in"", (""time"", PlayerTime(remaining))) + updateNote);
                }
                private static string BuildBroadcast(string message)
                {
                    return $""broadcast center {message}"";
                }";

            Assert.Empty(RawEnglish(after, @"SendCountdownBroadcastAsync\s*\("));
            Assert.Empty(RawEnglishInBody(after, "BuildBroadcast"));
        }
    }
}
