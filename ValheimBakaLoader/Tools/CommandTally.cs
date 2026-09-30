using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// How many times each host command was pressed since the last beat, per server profile.
    /// </summary>
    public interface ICommandTally
    {
        /// <summary>
        /// Counts one command a host PRESSED, named by the bridge method it arrived on and the
        /// profile it was about. Anything that is not a command is ignored, so the caller may
        /// hand every method that comes over the bridge.
        /// <para>
        /// Pressed and not ran, deliberately: the count is taken on the way in, before the
        /// handler runs, so a command the server refused is still one press. That is the honest
        /// reading of "which parts of the window get used", and it is the word every sentence
        /// about these counts uses, in the app, in the README and on the wiki.
        /// </para>
        /// </summary>
        void Count(string method, string profileName, string argument = null);

        /// <summary>
        /// What has been counted since the last <see cref="Forget"/>, keyed by the profile's
        /// one-way key. Empty when nothing has been issued, which is when a beat leaves the
        /// whole field out.
        /// </summary>
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Snapshot();

        /// <summary>
        /// Takes a snapshot the backend acknowledged back out of the tally. It SUBTRACTS rather
        /// than clearing, so a command issued while the beat was in flight is still counted on
        /// the next one instead of being thrown away with the reply.
        /// </summary>
        void Forget(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> acknowledged);
    }

    /// <summary>
    /// The count of each command a host issued, per server profile, for the anonymous beat.
    /// <para>
    /// WHAT IS IN IT AND WHAT IS NOT. Counts, and nothing else: how many times Kick was
    /// pressed, not who was kicked; how many mods were removed, not which ones; how many
    /// console lines were sent, and the verb on them, never the arguments. The profile is a
    /// one-way key over a random salt this install keeps to itself and the profile name
    /// together, so two servers on one machine are told apart without either name leaving it,
    /// and the same server on two machines is not recognisable as the same server. The salt was
    /// the install's anonymous id until 1.2.6, which travels in the same beat as the counts and
    /// so was no salt at all: see <see cref="AnalyticsSalt"/>.
    /// </para>
    /// <para>
    /// WHY IT IS CAPPED. One of the commands counted is the free-typed console line, and its
    /// verb is whatever a host typed. Without a ceiling a host who mistypes for an evening
    /// would grow the payload a key at a time, and the beat is meant to be a couple of hundred
    /// bytes. Past the cap the counting stops for that profile rather than the whole tally: the
    /// sixty-fourth key is the last one, and the ones already there keep counting.
    /// </para>
    /// <para>
    /// It lives in memory and nowhere else. Nothing here is written to disk, so closing the app
    /// throws away whatever had not been sent, which is the right way round for a count nobody
    /// needs.
    /// </para>
    /// </summary>
    public sealed class CommandTally : ICommandTally
    {
        /// <summary>The most distinct command keys one profile may carry between two beats.</summary>
        public const int MaxKeysPerProfile = 64;

        /// <summary>The longest a console verb may be before it is cut, so one line cannot grow the beat.</summary>
        internal const int MaxVerbLength = 24;

        /// <summary>
        /// Every command a host can issue, and the short name it is counted under.
        /// <para>
        /// A named table and not "every method that comes over the bridge". Most of what comes
        /// over the bridge is the page READING something, several times a second, and counting
        /// those would say nothing about what a host does and everything about how often a
        /// timer ran. So the list is the palette entries and the hall buttons: the presses a
        /// host makes on purpose.
        /// </para>
        /// <para>
        /// The names are short and stable and they are NOT the method names, because a method
        /// can be renamed and a count that changes its name looks like a feature nobody uses
        /// any more.
        /// </para>
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, string> Commands =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // the Players hall
                ["players.kick"] = "kick",
                ["players.cleanse"] = "cleanse",
                ["players.cleanseWhenEmpty"] = "cleanseArm",
                ["players.heal"] = "heal",
                ["players.smite"] = "smite",
                ["players.teleport"] = "teleport",
                ["players.spawn"] = "spawn",
                ["players.setList"] = "setList",
                ["players.remove"] = "playerForget",

                // the server itself
                ["server.start"] = "start",
                ["server.stop"] = "stop",
                ["server.restart"] = "restart",
                ["server.broadcast"] = "broadcast",
                ["server.update"] = "serverUpdate",
                ["server.updateCancel"] = "serverUpdateCancel",
                ["app.selfUpdateNow"] = "appUpdate",

                // worlds, backups and settings
                ["worldgen.save"] = "worldSettingsSave",
                ["maxplayers.save"] = "maxPlayersSave",
                ["profiles.save"] = "realmSave",
                ["backups.restore"] = "restore",
                ["backups.delete"] = "backupDelete",
                ["worlds.delete"] = "worldDelete",
                ["worlds.copyAs"] = "copyWorld",
                ["servers.create"] = "duplicate",
                ["servers.adoptWorld"] = "adoptWorld",
                ["config.write"] = "configWrite",
                ["atlas.render"] = "mapRender",
                ["analytics.reset"] = "statsReset",

                // mods and the loader
                ["mods.scan"] = "modScan",
                ["mods.update"] = "modUpdate",
                ["mods.updateAll"] = "modUpdateAll",
                ["mods.remove"] = "modRemove",
                ["mods.addFromUrl"] = "modInstall",
                ["mods.installFromHexium"] = "modInstallHexium",
                ["caps.install"] = "modInstallRequired",
                ["bepinex.install"] = "bepinexInstall",
                ["bepinex.update"] = "bepinexRepair",
                ["bepinex.restore"] = "bepinexRestore",
                ["bepinex.remove"] = "bepinexRemove",

                // the rest of the window
                ["net.diagnose"] = "connectionTest",
                ["lang.download"] = "langDownload",
                ["discord.publish"] = "heraldPublish",
                ["discord.remove"] = "heraldRemove",
            };

        /// <summary>
        /// The one method whose ARGUMENT names the command: the free-typed console line, and
        /// every palette entry that is a console line rather than a button. Its verb is counted
        /// under a prefix of its own so a verb can never collide with a name in the table above.
        /// </summary>
        internal const string ConsoleMethod = "server.command";

        /// <summary>The prefix an RCON verb is counted under.</summary>
        internal const string ConsolePrefix = "rcon.";

        private readonly object Gate = new();
        private readonly Dictionary<string, Dictionary<string, int>> Counts =
            new(StringComparer.Ordinal);

        private readonly IUserPreferencesProvider UserPrefsProvider;

        /// <summary>
        /// The salt this process uses when there are no preferences to keep one in, which is a
        /// test and never a running install. Random rather than empty on purpose: a salt
        /// anybody can guess is the very thing the stored one exists to stop.
        /// </summary>
        private static readonly string ProcessSalt = AnalyticsSalt.New();

        private string HeldSalt;

        public CommandTally(IUserPreferencesProvider userPrefsProvider = null)
            => UserPrefsProvider = userPrefsProvider;

        /// <summary>
        /// This install's salt, read once and held: a press must not cost a read of
        /// userprefs.json, and the value cannot change under a running app.
        /// </summary>
        private string Salt()
        {
            if (HeldSalt != null) return HeldSalt;

            lock (Gate)
            {
                HeldSalt ??= UserPrefsProvider == null
                    ? ProcessSalt
                    : AnalyticsSalt.Ensure(UserPrefsProvider);
            }

            return HeldSalt;
        }

        /// <summary>
        /// The one-way key a profile is counted under: the first sixteen hex characters of a
        /// SHA-256 over this install's SALT and the profile name together.
        /// <para>
        /// The salt is in it so the same profile name on two machines is two keys, and the
        /// profile name is in it so two servers on one machine are two keys. Neither can be
        /// read back out of it, and sixteen characters is plenty to tell a handful of profiles
        /// apart while being far too short to walk backwards through a dictionary of every
        /// server name in the world.
        /// </para>
        /// <para>
        /// THE SALT IS NOT THE DEVICE HASH, and that is the whole of this method's history. It
        /// used to be: a digest over <c>AssemblyHelper.GetClientCorrelationId()</c> and the
        /// name. That id travels in the SAME beat as these counts, as <c>deviceHash</c>, so
        /// anybody holding one beat held the salt and could hash a wordlist of server names
        /// against it until one matched. A one-way key whose salt is published beside it is not
        /// one. The salt is a random value made once per install, kept in userprefs.json and
        /// never sent anywhere: see <see cref="AnalyticsSalt"/>. The key stays stable per
        /// install, which is all the beat ever needed of it.
        /// </para>
        /// </summary>
        public static string ProfileKey(string profileName, string salt)
        {
            var seed = (salt ?? "") + "\u0000" + (profileName ?? "");
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(digest).Substring(0, 16).ToLowerInvariant();
        }

        /// <summary>
        /// The name one bridge method is counted under, or null when it is not a command. The
        /// console line's verb comes out of its argument; every other name is fixed.
        /// </summary>
        internal static string NameOf(string method, string argument)
        {
            if (string.IsNullOrEmpty(method)) return null;

            if (string.Equals(method, ConsoleMethod, StringComparison.Ordinal))
            {
                var verb = Verb(argument);
                return verb == null ? null : ConsolePrefix + verb;
            }

            return Commands.TryGetValue(method, out var name) ? name : null;
        }

        /// <summary>
        /// The verb off a console line: the first word, lower case, cut to
        /// <see cref="MaxVerbLength"/>, and made of plain ASCII letters, digits, underscores and
        /// dots and nothing else. Everything after the first space is an argument and never
        /// travels.
        /// <para>
        /// ANY character outside plain ASCII drops the WHOLE line. This is the only counted
        /// command whose key a host TYPES, so it is the only one that can carry a word off the
        /// machine, and a Valheim player name is very often written in a script the old filter
        /// said yes to: <c>char.IsLetterOrDigit</c> answers for the whole of Unicode, so every
        /// Cyrillic, Greek, CJK and accented Latin letter was kept and lower-cased. Dropping
        /// those characters instead would leave most of a name behind, and half a name is still
        /// a name. The test is on the character and not on its Unicode category, because a name
        /// written with a combining accent is a name whose letters are each plain ASCII.
        /// ASCII punctuation is still simply dropped, so a host who types <c>/kick</c> is
        /// counted under kick the way one who types <c>kick</c> is.
        /// </para>
        /// </summary>
        internal static string Verb(string line)
        {
            var text = (line ?? "").Trim();
            if (text.Length == 0) return null;

            var space = text.IndexOfAny(new[] { ' ', '\t' });
            if (space > 0) text = text.Substring(0, space);

            var kept = new StringBuilder(MaxVerbLength);
            foreach (var c in text)
            {
                // Read past the ceiling rather than breaking at it: a character this build
                // cannot spell has to drop the line whether it sits in the first position or
                // the fortieth.
                if (c > 127) return null;
                if (kept.Length >= MaxVerbLength) continue;
                if (IsPlainVerbChar(c)) kept.Append(char.ToLowerInvariant(c));
            }

            return kept.Length == 0 ? null : kept.ToString();
        }

        /// <summary>
        /// One character a console key may be made of: <c>[A-Za-z0-9_.]</c>, which is the shape
        /// the README promises and the shape the tests assert.
        /// </summary>
        private static bool IsPlainVerbChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
            || c == '_' || c == '.';

        public void Count(string method, string profileName, string argument = null)
        {
            var name = NameOf(method, argument);
            if (name == null) return;

            var key = ProfileKey(profileName, Salt());

            lock (Gate)
            {
                if (!Counts.TryGetValue(key, out var forProfile))
                {
                    forProfile = new Dictionary<string, int>(StringComparer.Ordinal);
                    Counts[key] = forProfile;
                }

                if (!forProfile.ContainsKey(name) && forProfile.Count >= MaxKeysPerProfile) return;

                forProfile.TryGetValue(name, out var was);
                forProfile[name] = was + 1;
            }
        }

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Snapshot()
        {
            lock (Gate)
            {
                return Counts
                    .Where(profile => profile.Value.Count > 0)
                    .ToDictionary(
                        profile => profile.Key,
                        profile => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(
                            profile.Value, StringComparer.Ordinal),
                        StringComparer.Ordinal);
            }
        }

        public void Forget(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> acknowledged)
        {
            if (acknowledged == null) return;

            lock (Gate)
            {
                foreach (var profile in acknowledged)
                {
                    if (!Counts.TryGetValue(profile.Key, out var forProfile)) continue;

                    foreach (var command in profile.Value)
                    {
                        if (!forProfile.TryGetValue(command.Key, out var held)) continue;

                        // Subtract, never assign. A press that landed while the beat was in
                        // flight is still on the tally and belongs on the next one.
                        var left = held - command.Value;
                        if (left > 0) forProfile[command.Key] = left;
                        else forProfile.Remove(command.Key);
                    }

                    if (forProfile.Count == 0) Counts.Remove(profile.Key);
                }
            }
        }
    }
}
