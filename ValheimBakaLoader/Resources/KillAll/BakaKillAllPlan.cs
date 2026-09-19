// BakaLoader KillAll: the half of the sweep that needs no game at all.
//
// This file is deliberately plain C#. It names no Unity type, no BepInEx type and no
// Valheim type, so it compiles three ways without a single conditional: into
// BakaKillAll.dll and into BakaLoaderCommander.dll through Resources\build-plugins.ps1,
// and into BakaLoader itself through the app project's ordinary source glob. That third
// way is the point: the test suite can reach these types through the assembly's
// InternalsVisibleTo and hold the argument parsing, the spare rule and the reply text to
// account on every build, on a machine with no dedicated server installed.
//
// Its companion BakaKillAllSweep.cs is the half that does touch the game, and that one is
// fenced off behind the VALHEIM_PLUGIN symbol because the app has no game assemblies to
// compile it against. KEEP THIS FILE FREE OF GAME TYPES: a using of UnityEngine here stops
// BakaLoader itself from building, and CompanionPluginSourceTests fails the moment one
// appears.
using System;
using System.Globalization;
using System.Text;

namespace BakaLoaderKillAll
{
    /// <summary>What a single baka_killall is allowed to touch.</summary>
    internal enum KillAllScope
    {
        /// <summary>Every hostile in the world. Plain "baka_killall".</summary>
        Everything,

        /// <summary>One named prefab. "baka_killall Eikthyr".</summary>
        OnePrefab,

        /// <summary>Everything hostile within a radius of a player. "baka_killall near Mithi 50".</summary>
        NearPlayer
    }

    /// <summary>
    /// A mirror of the game's Character.Faction, held here so the spare rule can be tested
    /// on a machine that has no game assemblies to load.
    /// <para>
    /// The mapping is done BY NAME in the sweep, never by number, so the values below are
    /// this file's own and a game update that reorders the real enum cannot quietly move a
    /// faction from one row of the rule to another. A faction the game has and this list has
    /// not becomes <see cref="Unknown"/>, and a faction this list has and the game has not is
    /// a compile error in the sweep, which is exactly the loud half of the deal.
    /// </para>
    /// </summary>
    internal enum KillAllFaction
    {
        Unknown = 0,
        Players,
        AnimalsVeg,
        ForestMonsters,
        Undead,
        Demon,
        MountainMonsters,
        SeaMonsters,
        PlainsMonsters,
        Boss,
        MistlandsMonsters,
        Dverger,
        PlayerSpawned,
        TrainingDummy,
        DeepNorth
    }

    /// <summary>The one extra sentence a reply may carry, when the counts alone would mislead.</summary>
    internal enum KillAllNote
    {
        None,

        /// <summary>The host named something this game has never heard of.</summary>
        NoSuchPrefab,

        /// <summary>The host named something real that KillAll is never allowed to touch.</summary>
        PrefabIsNotHostile,

        /// <summary>The host named a real hostile, and not one of them is in the world.</summary>
        NoneInWorld,

        /// <summary>The host asked for a radius, and nothing hostile stood inside it.</summary>
        NoneInRange
    }

    /// <summary>One parsed baka_killall, or the sentence to send back instead of running it.</summary>
    internal sealed class KillAllRequest
    {
        public KillAllScope Scope = KillAllScope.Everything;

        /// <summary>The prefab the host named, exactly as typed. Only for <see cref="KillAllScope.OnePrefab"/>.</summary>
        public string PrefabName = "";

        /// <summary>The player the radius is measured from. Only for <see cref="KillAllScope.NearPlayer"/>.</summary>
        public string PlayerName = "";

        /// <summary>The radius in metres. Only for <see cref="KillAllScope.NearPlayer"/>.</summary>
        public float Radius;

        /// <summary>Null when the request is runnable, otherwise the whole reply to send.</summary>
        public string Error;

        public bool IsValid
        {
            get { return Error == null; }
        }
    }

    /// <summary>
    /// Argument parsing, the spare rule and the reply text. Everything here is a pure
    /// function of its arguments, which is what makes the rules testable at all: the sweep
    /// itself can only be exercised against a running dedicated server.
    /// </summary>
    internal static class KillAllPlan
    {
        internal const string Usage =
            "Usage: baka_killall for every hostile, baka_killall <PrefabName> for one kind, " +
            "or baka_killall near <player> <radius> for everything hostile around somebody.";

        internal const string BadRadius =
            "Error: the radius has to be a number of metres above zero, like 50.";

        /// <summary>The word that turns the second argument into a radius rather than a prefab.</summary>
        internal const string NearKeyword = "near";

        /// <summary>
        /// Reads one console or RCON line, already split on spaces with the empty pieces
        /// dropped, and with the verb still sitting at index 0 the way both the game's
        /// Terminal and Commander hand it over.
        /// <para>
        /// A player name may hold spaces, so in the radius form the radius is taken off the
        /// END and everything between "near" and it is the name. A prefab name never holds
        /// spaces, so the plain form takes exactly one extra word and anything longer is a
        /// usage error rather than a silent guess at which word was meant.
        /// </para>
        /// </summary>
        internal static KillAllRequest Parse(string[] tokens)
        {
            var request = new KillAllRequest();

            if (tokens == null || tokens.Length <= 1) return request;

            if (string.Equals(tokens[1], NearKeyword, StringComparison.OrdinalIgnoreCase))
            {
                // near <player> <radius> is four words at the least, and the name is what
                // lies between the keyword and the number.
                if (tokens.Length < 4)
                {
                    request.Error = Usage;
                    return request;
                }

                float radius;
                if (!float.TryParse(tokens[tokens.Length - 1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out radius)
                    || float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
                {
                    request.Error = BadRadius;
                    return request;
                }

                request.Scope = KillAllScope.NearPlayer;
                request.PlayerName = string.Join(" ", tokens, 2, tokens.Length - 3);
                request.Radius = radius;
                return request;
            }

            if (tokens.Length > 2)
            {
                request.Error = Usage;
                return request;
            }

            request.Scope = KillAllScope.OnePrefab;
            request.PrefabName = tokens[1];
            return request;
        }

        /// <summary>
        /// Kill-all is for hostiles. Players, tamed animals and the friendly factions are
        /// never targets, and neither is a player-built training post: it is its own faction
        /// in the game and it is a structure somebody put up, not a creature that wandered
        /// in. Boss is a hostile faction and stays killable.
        /// <para>
        /// Every fact this takes comes off the world's own record of the creature rather than
        /// off a Character object, because on a dedicated server the Character objects for
        /// everything around a player live in that player's process, not in the server's. See
        /// BakaKillAllSweep.cs for why that is the whole bug this rule was rewritten for.
        /// </para>
        /// <para>
        /// A faction the game has grown and this plugin has not is SPARED, not killed. The
        /// two mistakes are not the same size: a hostile that survives a sweep is a thing the
        /// host can see and say out loud, and a tamed companion or a friendly NPC that a
        /// sweep deleted is gone from the save with nobody told. The reply says how many were
        /// left standing for that reason so the gap gets reported rather than lived with.
        /// </para>
        /// </summary>
        internal static bool ShouldSpare(bool isPlayer, bool isTamed, KillAllFaction faction)
        {
            if (isPlayer) return true;
            if (isTamed) return true; // pets: wolves, boars, lox, modded companions

            switch (faction)
            {
                case KillAllFaction.Players:       // player-faction NPCs (many modded friendlies)
                case KillAllFaction.AnimalsVeg:    // passive wildlife (deer, gulls, hares)
                case KillAllFaction.Dverger:       // dvergr allies
                case KillAllFaction.PlayerSpawned: // player-summoned allies
                case KillAllFaction.TrainingDummy: // player-built training posts
                case KillAllFaction.Unknown:       // a faction this plugin has not met yet
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The whole reply, first words first.
        /// <para>
        /// "KillAll complete" has to stay at the front: it is what anything reading these
        /// replies matches on, and the three counts are the three outcomes a host cares
        /// about. Out of reach is its own number rather than folded into either of the
        /// others, because a creature nobody has loaded cannot be damaged by anybody, and
        /// counting it as slain is the lie that hid the bug this command was rewritten for.
        /// </para>
        /// </summary>
        internal static string Reply(int slain, int unreachable, int spared,
            KillAllNote note, string subject, int unknownFactions)
        {
            var text = new StringBuilder("KillAll complete: ").Append(Counts(slain, unreachable, spared));

            var sentence = NoteSentence(note, subject);
            if (sentence.Length > 0) text.Append(". ").Append(sentence);

            var unknown = UnknownFactionSentence(unknownFactions);
            if (unknown.Length > 0) text.Append(". ").Append(unknown);

            return text.ToString();
        }

        /// <summary>
        /// The three outcomes, written once and used by both the line a finished sweep sends
        /// and the line one that fell over sends. Spelling them twice is how the two drift, and
        /// the drift a host notices first is the one that reads "1 hostiles slain".
        /// </summary>
        internal static string Counts(int slain, int unreachable, int spared)
        {
            var text = new StringBuilder();
            text.Append(slain).Append(slain == 1 ? " hostile slain, " : " hostiles slain, ");
            text.Append(unreachable).Append(" out of reach, ");
            text.Append(spared).Append(" spared (players, pets & allies)");
            return text.ToString();
        }

        /// <summary>
        /// What a sweep says the moment it starts, when it is too big to finish inside the
        /// answer.
        /// <para>
        /// RCON is why this line exists. BakaLoader's client gives up at five seconds and
        /// Commander stops waiting at four and a half, and until this was written a sweep that
        /// ran past that answered "Error: command timed out" while the sweep itself carried on
        /// and did every bit of its work. A host was told the command had failed when it had
        /// not. The sweep now answers the moment it knows what it is about to walk, and the
        /// result line follows in the server log. A candidate is one creature record inside the
        /// scope the host asked for, which is the number that decides how long the walk takes.
        /// </para>
        /// </summary>
        internal static string Started(int candidates)
        {
            return "KillAll started: " + candidates + (candidates == 1 ? " candidate" : " candidates") +
                   ". The result line follows in the server log when the sweep finishes.";
        }

        /// <summary>
        /// The answer to a second baka_killall while the first is still walking. Starting a
        /// second sweep over the same snapshot would double every count and strike half the
        /// world twice, so the second command is refused and says how far the first has to go.
        /// </summary>
        internal static string AlreadyRunning(int remaining)
        {
            return "KillAll is already running: " + remaining +
                   (remaining == 1 ? " candidate" : " candidates") +
                   " still to go. Wait for its result line before starting another.";
        }

        /// <summary>
        /// What a sweep says when something threw part way through the walk. The counts it had
        /// reached are real work that really happened, so they are reported rather than thrown
        /// away, and the line does NOT open with "KillAll complete" because it is not one.
        /// </summary>
        internal static string StoppedEarly(int slain, int unreachable, int spared, string fault)
        {
            var reason = string.IsNullOrEmpty(fault) ? "no reason given" : fault;
            return "KillAll stopped early: " + reason + ". Up to that point: " +
                   Counts(slain, unreachable, spared) + ".";
        }

        /// <summary>The sentence for a note, or an empty string when the counts say it all.</summary>
        internal static string NoteSentence(KillAllNote note, string subject)
        {
            var name = string.IsNullOrEmpty(subject) ? "that" : subject;

            switch (note)
            {
                case KillAllNote.NoSuchPrefab:
                    return "There is no creature called '" + name + "' in this game.";
                case KillAllNote.PrefabIsNotHostile:
                    return name + " is not a hostile creature, so KillAll leaves it where it stands.";
                case KillAllNote.NoneInWorld:
                    return "No " + name + " is in the world right now.";
                case KillAllNote.NoneInRange:
                    return "Nothing hostile was standing inside that radius around " + name + ".";
                default:
                    return "";
            }
        }

        /// <summary>
        /// What to say when the sweep met a faction it has no rule for. Silence here would be
        /// the worst of it: the creature is counted as spared, the host reads a number that
        /// looks like mercy towards a pet, and the plugin never gets told it is out of date.
        /// </summary>
        internal static string UnknownFactionSentence(int unknownFactions)
        {
            if (unknownFactions <= 0) return "";

            if (unknownFactions == 1)
                return "One of them belongs to a faction this KillAll does not know yet, so it was left standing.";

            return unknownFactions +
                   " of them belong to a faction this KillAll does not know yet, so they were left standing.";
        }
    }
}
