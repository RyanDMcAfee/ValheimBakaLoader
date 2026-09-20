using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// What reading a world's own starting keys came to: the dials and switches they turn out
    /// to be, the keys no dial and no switch accounts for, and the game's own summary lines
    /// that were left where they were.
    /// </summary>
    public sealed class WorldKeyImportResult
    {
        /// <summary>The dials the keys spell out, ready to be stored as world preferences.</summary>
        public Dictionary<string, string> Modifiers { get; init; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Everything that reaches the command line as -setkey: the switches that were on, and
        /// every key no dial and no switch accounts for.
        /// </summary>
        public HashSet<string> Keys { get; init; } = new(StringComparer.Ordinal);

        /// <summary>The switches that were on, on their own, so a notice can name them.</summary>
        public List<string> Switches { get; init; } = new();

        /// <summary>The keys carried through untouched, on their own, for the same reason.</summary>
        public List<string> PassThrough { get; init; } = new();

        /// <summary>The game's own "preset ..." summary lines, named but never imported.</summary>
        public List<string> Summaries { get; init; } = new();

        /// <summary>True when the header held anything worth bringing in.</summary>
        public bool Anything => Modifiers.Count > 0 || Keys.Count > 0;
    }

    /// <summary>What a first meeting came to, which is four things and not two.</summary>
    public enum WorldKeyImportKind
    {
        /// <summary>
        /// No header, or one that would not read that far. Nothing is brought in, nothing is
        /// written, and the start goes ahead exactly as it always did.
        /// </summary>
        NoHeader,

        /// <summary>
        /// The profile already holds settings for this world, so they are the host's own
        /// choice and the header is never read over them. This is not a first meeting.
        /// </summary>
        AlreadyKnown,

        /// <summary>A world with no modifiers on it. Nothing to bring in and nothing to say.</summary>
        NothingToBringIn,

        /// <summary>The world's own settings were read and stored.</summary>
        Imported,
    }

    /// <summary>What <see cref="WorldKeyImportStep.Run"/> did, and what it read on the way.</summary>
    public sealed class WorldKeyImportOutcome
    {
        public WorldKeyImportKind Kind { get; init; }

        /// <summary>The reading, on an <see cref="WorldKeyImportKind.Imported"/>; null otherwise.</summary>
        public WorldKeyImportResult Imported { get; init; }

        /// <summary>What the profile already held, on an <see cref="WorldKeyImportKind.AlreadyKnown"/>.</summary>
        public WorldPreferences Existing { get; init; }
    }

    /// <summary>
    /// The first-meeting step itself: ask the profile, and only if it has never heard of this
    /// world, read the world's own header and store what it holds.
    /// <para>
    /// It is a static over its two providers rather than a method on the window because it sits
    /// on EVERY start path, the unattended ones included, and a step that can only be reached
    /// through a window is a step that can only be proved through one. The window keeps the
    /// notice, the event and the log line; the decision and the write are here.
    /// </para>
    /// </summary>
    public static class WorldKeyImportStep
    {
        /// <summary>
        /// Runs the step for one world. Never throws on a header it cannot read: that is
        /// <see cref="WorldKeyImportKind.NoHeader"/> and the caller carries on.
        /// </summary>
        /// <param name="worlds">The world-preferences store, asked first and written to last.</param>
        /// <param name="readHeaderKeys">
        /// The world's own starting keys, read only when the profile has never heard of it.
        /// Null from this means the header could not be read.
        /// </param>
        /// <param name="world">The world name, as the profile spells it.</param>
        public static WorldKeyImportOutcome Run(
            IWorldPreferencesProvider worlds,
            Func<IReadOnlyList<string>> readHeaderKeys,
            string world)
        {
            if (worlds == null) throw new ArgumentNullException(nameof(worlds));
            if (readHeaderKeys == null) throw new ArgumentNullException(nameof(readHeaderKeys));
            if (string.IsNullOrWhiteSpace(world))
                return new WorldKeyImportOutcome { Kind = WorldKeyImportKind.NoHeader };

            var existing = worlds.LoadPreferences(world);
            if (existing != null)
                return new WorldKeyImportOutcome { Kind = WorldKeyImportKind.AlreadyKnown, Existing = existing };

            var headerKeys = readHeaderKeys();
            if (headerKeys == null)
                return new WorldKeyImportOutcome { Kind = WorldKeyImportKind.NoHeader };

            var imported = WorldKeyImport.FromHeaderKeys(headerKeys);
            if (!imported.Anything)
            {
                // Writing an empty entry would be honest and would also mean this never looks
                // again, so a world that is simply bare is left alone rather than claimed.
                return new WorldKeyImportOutcome { Kind = WorldKeyImportKind.NothingToBringIn };
            }

            worlds.SavePreferences(new WorldPreferences
            {
                WorldName = world,
                Preset = null,
                Modifiers = imported.Modifiers,
                Keys = imported.Keys,
            });

            return new WorldKeyImportOutcome { Kind = WorldKeyImportKind.Imported, Imported = imported };
        }
    }

    /// <summary>
    /// Reads a world's OWN world-modifier settings back out of the starting keys its header
    /// carries, so a world made in the game client (or set from the console) can be brought
    /// into BakaLoader rather than wiped by its first BakaLoader start.
    /// <para>
    /// The game keeps no record of which dial was moved. It writes the KEYS a dial value stands
    /// for and nothing else, so reading a dial back means recognising a key set:
    /// <c>playerdamage 85</c> beside <c>enemydamage 150</c> beside <c>enemyspeedsize 110</c>
    /// beside <c>enemyleveluprate 120</c> is Combat on Hard. <see cref="WorldGen.DialKeys"/> is
    /// that table.
    /// </para>
    /// <para>
    /// Longest set first, and the matched keys are taken out of the pool, which is the same
    /// rule the game's own summary line uses. It matters on one pair: Death penalty Casual is
    /// <c>deathkeepequip</c> plus <c>skillreductionrate 15</c> and Very easy is that second key
    /// alone, so a shortest-first reading would call a Casual world Very easy and quietly drop
    /// the key that keeps a viking's gear on.
    /// </para>
    /// <para>
    /// Nothing is ever dropped. A key that matches no dial and is not one of the five switches
    /// is carried through exactly as it was read, which is what keeps a world that carries
    /// <c>carryweightrate 150</c> from losing it the first time BakaLoader starts it.
    /// </para>
    /// </summary>
    public static class WorldKeyImport
    {
        /// <summary>
        /// The dials, switches and pass-through keys a header's starting keys amount to.
        /// Never throws: a null or empty list reads as a world with nothing on it.
        /// </summary>
        public static WorldKeyImportResult FromHeaderKeys(IEnumerable<string> headerKeys)
        {
            var result = new WorldKeyImportResult();
            if (headerKeys == null) return result;

            // Lower case and trimmed, because that is how the game writes them and how every
            // comparison below is spelled. Blanks and repeats fall out here.
            var pool = new List<string>();
            foreach (var raw in headerKeys)
            {
                var key = (raw ?? "").Trim().ToLowerInvariant();
                if (key.Length == 0) continue;
                if (WorldGen.IsPresetSummary(key))
                {
                    // The screen's own label for the other keys. Importing it would send the
                    // game a description of a choice instead of the choice.
                    if (!result.Summaries.Contains(key, StringComparer.Ordinal)) result.Summaries.Add(key);
                    continue;
                }
                if (!pool.Contains(key, StringComparer.Ordinal)) pool.Add(key);
            }

            foreach (var dial in WorldGen.DialKeys)
            {
                var match = dial.Value
                    .Where(option => option.Value.All(key => pool.Contains(key, StringComparer.Ordinal)))
                    .OrderByDescending(option => option.Value.Length)
                    .ThenBy(option => option.Key, StringComparer.Ordinal)
                    .Select(option => (string)option.Key)
                    .FirstOrDefault();

                if (match == null) continue;

                result.Modifiers[dial.Key] = match;
                foreach (var key in dial.Value[match]) pool.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
            }

            foreach (var key in pool)
            {
                result.Keys.Add(key);
                if (WorldGen.IsSwitch(key)) result.Switches.Add(key);
                else result.PassThrough.Add(key);
            }

            result.Switches.Sort(StringComparer.Ordinal);
            result.PassThrough.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// The keys a set of stored preferences would put back on the command line, in the same
        /// spelling a header carries them in. This is the other half of the reading above: it is
        /// what lets a later start ask whether the world still holds what BakaLoader thinks it
        /// does without a second table to keep in step.
        /// </summary>
        public static List<string> KeysFor(WorldPreferences prefs)
        {
            var keys = new List<string>();
            if (prefs == null) return keys;

            // A preset is the one shape this cannot spell out: the game expands it inside its
            // own menu and BakaLoader stores only the word. Sending nothing for it is honest;
            // the comparison that reads this treats it as "no answer" rather than "no keys".
            if (string.IsNullOrEmpty(prefs.Preset) && prefs.Modifiers != null)
            {
                foreach (var (dial, value) in prefs.Modifiers)
                {
                    if (!WorldGen.DialKeys.TryGetValue(dial, out var options)) continue;
                    if (!options.TryGetValue(value ?? "", out var dialKeys)) continue;
                    keys.AddRange(dialKeys);
                }
            }

            if (prefs.Keys != null) keys.AddRange(prefs.Keys.Select(k => (k ?? "").Trim().ToLowerInvariant()));

            return keys
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The header's keys as they would be compared against <see cref="KeysFor"/>: the same
        /// normalising, with the game's own summary lines left out because BakaLoader never
        /// writes one and their absence is not a disagreement.
        /// </summary>
        public static List<string> ComparableHeaderKeys(IEnumerable<string> headerKeys)
            => (headerKeys ?? Enumerable.Empty<string>())
                .Select(k => (k ?? "").Trim().ToLowerInvariant())
                .Where(k => k.Length > 0 && !WorldGen.IsPresetSummary(k))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
    }
}
