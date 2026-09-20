using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Data;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// Describes a player by whatever identity fragments a server log line
    /// happened to carry. Blank fields match anything; a chained <see cref="Or"/>
    /// clause widens the match.
    /// </summary>
    public class PlayerDataQuery
    {
        public string Platform;

        public string PlayerId;

        public string PlayerName;

        public string ZdoId;

        public string CharacterName;

        public PlayerDataQuery Or;

        public bool HasParameters() => !string.IsNullOrWhiteSpace(ToString());

        public override string ToString()
        {
            var self = string.Join("&", Fields()
                .Where(f => !string.IsNullOrWhiteSpace(f.value))
                .Select(f => $"{f.name}={f.value}"));

            var rest = Or?.ToString();
            return string.IsNullOrWhiteSpace(rest) ? self : $"{self}|{rest}";
        }

        private IEnumerable<(string name, string value)> Fields()
        {
            yield return (nameof(Platform), Platform);
            yield return (nameof(PlayerId), PlayerId);
            yield return (nameof(PlayerName), PlayerName);
            yield return (nameof(ZdoId), ZdoId);
            yield return (nameof(CharacterName), CharacterName);
        }
    }

    public interface IPlayerDataRepository : IDataRepository<PlayerInfo>
    {
        event EventHandler<PlayerInfo> PlayerStatusChanged;

        IEnumerable<PlayerInfo> FindPlayersByQuery(PlayerDataQuery query);

        PlayerInfo SetPlayerJoining(PlayerDataQuery query, string serverKey = null);

        PlayerInfo SetPlayerOnline(string characterName, string zdoId, string serverKey = null);

        void SetPlayerNumericId(string characterName, string numericId, string serverKey = null);

        void SetPlayerLeaving(PlayerDataQuery query, string serverKey = null);

        void SetPlayerOffline(PlayerDataQuery query, string serverKey = null);

        Task LoadAsync();
    }

    /// <summary>
    /// The players.json collection, plus the identity resolution that stitches
    /// together Valheim's fragmented log lines: connection events carry a
    /// platform id, character-spawn events carry only a character name, and
    /// nothing ties the two directly. Resolution is by prior sightings of the
    /// character name, falling back to "whoever is joining right now".
    /// </summary>
    public class PlayerDataRepository : KeyedJsonRepository<PlayerInfo>, IPlayerDataRepository
    {
        private readonly IRemoteApiClient Remote;

        // Status seen at the last save, per key. Lets us tell a real status
        // transition apart from a re-save of the same status.
        private readonly Dictionary<string, PlayerStatus> KnownStatuses = new();

        public PlayerDataRepository(
            IFileProvider fileProvider,
            ILogger logger,
            IRemoteApiClient remoteApiClient)
            : base(fileProvider, logger, Resources.PlayerListFilePath)
        {
            Remote = remoteApiClient;
            Remote.PlayerInfoAvailable += OnPlayerInfoAvailable;
            EntityUpdated += TrackStatusTransition;
        }

        public event EventHandler<PlayerInfo> PlayerStatusChanged;

        public IEnumerable<PlayerInfo> FindPlayersByQuery(PlayerDataQuery query)
        {
            var clauses = new List<PlayerDataQuery>();
            for (var q = query; q != null; q = q.Or)
            {
                clauses.Add(q);
            }

            return Data
                .Where(p => clauses.Any(q => Matches(p, q)))
                .DistinctBy(p => p.Key);
        }

        public PlayerInfo SetPlayerJoining(PlayerDataQuery query, string serverKey = null)
        {
            if (!query.HasParameters()) return null;

            // Prefer the matching identity that most recently disconnected.
            var offline = FindPlayersByQuery(query)
                .Where(p => p.PlayerStatus == PlayerStatus.Offline && SameServer(p, serverKey));
            var player = offline.MaxBy(p => p.LastStatusChange);

            if (player == null)
            {
                player = BuildPlayer(query);
                Logger.Information("New player joining: {query}", query);
            }
            else
            {
                Logger.Information("Known player joining: {query}", query);
            }

            player.PlayerStatus = PlayerStatus.Joining;
            player.LastStatusChange = DateTime.UtcNow;
            player.LastStatusCharacter = string.IsNullOrWhiteSpace(query.CharacterName) ? null : query.CharacterName;
            if (!string.IsNullOrWhiteSpace(serverKey)) player.ServerKey = serverKey;
            Upsert(player);

            if (string.IsNullOrWhiteSpace(player.PlayerName))
            {
                // Fire-and-forget; the answer arrives via PlayerInfoAvailable.
                Remote.RequestPlayerInfoAsync(player.Platform, player.PlayerId);
            }

            return player;
        }

        public PlayerInfo SetPlayerOnline(string characterName, string zdoId, string serverKey = null)
        {
            var byName = string.IsNullOrWhiteSpace(characterName)
                ? new List<PlayerInfo>()
                : FindPlayersByQuery(new() { CharacterName = characterName })
                    .Where(p => p.PlayerStatus is PlayerStatus.Joining or PlayerStatus.Offline)
                    .Where(p => SameServer(p, serverKey))
                    .ToList();

            var joining = Data
                .Where(p => p.PlayerStatus == PlayerStatus.Joining && SameServer(p, serverKey))
                .ToList();

            var (player, confident) = ResolveIdentity(characterName, byName, joining);
            if (player == null) return null;

            player.PlayerStatus = PlayerStatus.Online;
            player.LastStatusChange = DateTime.UtcNow;
            player.LastStatusCharacter = characterName;
            player.ZdoId = zdoId;
            player.AddCharacter(characterName, confident);
            if (!string.IsNullOrWhiteSpace(serverKey)) player.ServerKey = serverKey;
            Upsert(player);

            return player;
        }

        /// <summary>
        /// Records the numeric player id Valheim 1.0 prints for a connected peer
        /// ("Got player ID from Broheim : 1454938750"). That line carries only the
        /// character name, so the id lands on the connected player already known by
        /// that name; if the name has never been seen (a brand-new character) it
        /// falls back to the single player still connecting, and stays unrecorded
        /// when several are connecting at once rather than guessing wrong.
        /// </summary>
        public void SetPlayerNumericId(string characterName, string numericId, string serverKey = null)
        {
            if (string.IsNullOrWhiteSpace(numericId) || string.IsNullOrWhiteSpace(characterName)) return;

            var connected = Data
                .Where(p => p.PlayerStatus is PlayerStatus.Joining or PlayerStatus.Online)
                .Where(p => SameServer(p, serverKey))
                .ToList();
            if (connected.Count == 0) return;

            var matched = connected.Where(p => KnownByName(p, characterName)).ToList();

            if (matched.Count == 0)
            {
                var joining = connected.Where(p => p.PlayerStatus == PlayerStatus.Joining).ToList();
                if (joining.Count != 1)
                {
                    Logger.Information("Player id {id} for {name} could not be matched to a connected player",
                        numericId, characterName);
                    return;
                }

                matched = joining;
            }

            var changed = matched.Where(p => p.PlayerNumericId != numericId).ToList();
            if (changed.Count == 0) return;

            foreach (var player in changed)
            {
                player.PlayerNumericId = numericId;
            }

            UpsertBulk(changed);
            Logger.Information("Player id {id} recorded for {keys}",
                numericId, string.Join(", ", changed.Select(p => p.Key)));
        }

        /// <summary>True when this player has ever answered to the given name.</summary>
        private static bool KnownByName(PlayerInfo player, string name)
        {
            return string.Equals(player.LastStatusCharacter, name, StringComparison.Ordinal)
                || string.Equals(player.PlayerName, name, StringComparison.Ordinal)
                || player.Characters?.Any(c => c.CharacterName == name) == true;
        }

        public void SetPlayerLeaving(PlayerDataQuery query, string serverKey = null)
        {
            TransitionAll(query,
                p => p.PlayerStatus is PlayerStatus.Joining or PlayerStatus.Online,
                PlayerStatus.Leaving, "leaving", serverKey);
        }

        public void SetPlayerOffline(PlayerDataQuery query, string serverKey = null)
        {
            TransitionAll(query,
                p => p.PlayerStatus != PlayerStatus.Offline,
                PlayerStatus.Offline, "offline", serverKey);
        }

        public override async Task LoadAsync()
        {
            await base.LoadAsync();

            // Statuses are runtime-only, so everyone loads in as Offline.
            KnownStatuses.Clear();
            foreach (var player in Data)
            {
                KnownStatuses[player.Key] = player.PlayerStatus;
            }
        }

        /// <summary>
        /// Repairs the file on the way in. Every name in players-cache.json was learned from
        /// the server's console output, and until 1.2.0 that output was read with the machine's
        /// own code page rather than as UTF-8, so a name written in Greek, Cyrillic or Japanese
        /// was stored broken. Reading correctly from now on does not mend what is already on
        /// disk, and the roster, the menu and every action that names a player read from here.
        /// Nothing is written back by this: the next ordinary save carries the repair out.
        /// </summary>
        protected override Dictionary<string, PlayerInfo> OnLoaded(Dictionary<string, PlayerInfo> loaded)
        {
            var repaired = RepairNames(loaded, out var names, out var merged, TextRepair.AnsiPage);

            if (names > 0 || merged > 0)
            {
                Logger.Information(
                    "Players cache: repaired {names} name(s) stored in the wrong encoding and merged {merged} duplicate record(s)",
                    names, merged);
            }

            return repaired;
        }

        /// <summary>
        /// The same collection with every stored name put back the way it was written, and with
        /// any two records that turn out to be the same platform id folded into one.
        /// <para>
        /// The fold is the reason this is not a loop over <see cref="TextRepair.FixMojibake"/>:
        /// a player seen once before the encoding was fixed and once after can be sitting in the
        /// file twice, and two rows for one person is a roster that reads as two people and
        /// statistics split down the middle. The record that saw the player last is the one kept;
        /// the other fills in only what the kept one is missing, and their character lists are
        /// put together. Two spellings of one character inside a single record collapse the same
        /// way.
        /// </para>
        /// <para>Pure: it changes the records handed to it and answers what the file should hold.</para>
        /// <para>
        /// The page that did the damage is named rather than assumed. The app hands in
        /// <see cref="TextRepair.AnsiPage"/>, which is this machine's own and is what the old
        /// read used, and a test hands in the page its fixture was broken on. Without that a
        /// fixture broken on 1252 could only be checked on a 1252 machine, and on a Russian or
        /// Japanese one the test would fail for refusing a name it is RIGHT to refuse.
        /// </para>
        /// </summary>
        internal static Dictionary<string, PlayerInfo> RepairNames(
            Dictionary<string, PlayerInfo> loaded, out int names, out int merged, Encoding page)
        {
            names = 0;
            merged = 0;
            if (loaded == null) return new Dictionary<string, PlayerInfo>();

            var kept = new Dictionary<string, PlayerInfo>(loaded.Count);

            // The file key a platform id is already being kept under. A record's own key is
            // "{platform}:{id}", which is what every lookup in the app matches on, and it does
            // not have to be the key the file filed it under.
            var byIdentity = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var pair in loaded)
            {
                var player = pair.Value;
                if (player == null) continue;

                names += RepairOne(player, page);

                var identity = string.IsNullOrWhiteSpace(player.Platform)
                    || string.IsNullOrWhiteSpace(player.PlayerId)
                        ? null
                        : player.Key;

                if (identity != null && byIdentity.TryGetValue(identity, out var existing))
                {
                    MergeInto(kept[existing], player);
                    merged++;
                    continue;
                }

                kept[pair.Key] = player;
                if (identity != null) byIdentity[identity] = pair.Key;
            }

            return kept;
        }

        /// <summary>
        /// Repairs one record's names in place and answers how many of them changed. A character
        /// whose repaired name is one the record already holds is folded into it rather than
        /// left standing beside it, keeping the confident pairing of the two.
        /// </summary>
        private static int RepairOne(PlayerInfo player, Encoding page)
        {
            var changed = 0;

            var name = TextRepair.FixMojibake(player.PlayerName, page);
            if (!string.Equals(name, player.PlayerName, StringComparison.Ordinal)) changed++;
            player.PlayerName = name;

            var last = TextRepair.FixMojibake(player.LastStatusCharacter, page);
            if (!string.Equals(last, player.LastStatusCharacter, StringComparison.Ordinal)) changed++;
            player.LastStatusCharacter = last;

            if (player.Characters == null) return changed;

            var characters = new List<PlayerInfo.CharacterInfo>();
            foreach (var character in player.Characters)
            {
                if (character == null) continue;

                var repaired = TextRepair.FixMojibake(character.CharacterName, page);
                if (!string.Equals(repaired, character.CharacterName, StringComparison.Ordinal)) changed++;
                character.CharacterName = repaired;

                var already = characters.Find(c => c.CharacterName == character.CharacterName);
                if (already == null) characters.Add(character);
                else already.MatchConfident = already.MatchConfident || character.MatchConfident;
            }

            player.Characters = characters;
            return changed;
        }

        /// <summary>
        /// Folds the second record of one platform id into the first. Anything single valued
        /// comes from whichever of the two saw the player last, falling back to the other where
        /// that one has nothing to say, and every character either of them knows survives.
        /// </summary>
        private static void MergeInto(PlayerInfo keep, PlayerInfo other)
        {
            if (keep == null || other == null) return;

            var newer = other.LastStatusChange > keep.LastStatusChange ? other : keep;
            var older = ReferenceEquals(newer, other) ? keep : other;

            keep.PlayerName = Preferred(newer.PlayerName, older.PlayerName);
            keep.PlayerNumericId = Preferred(newer.PlayerNumericId, older.PlayerNumericId);
            keep.LastStatusCharacter = Preferred(newer.LastStatusCharacter, older.LastStatusCharacter);
            keep.ServerKey = Preferred(newer.ServerKey, older.ServerKey);
            keep.LastStatusChange = newer.LastStatusChange;

            if (other.Characters == null) return;

            foreach (var character in other.Characters)
            {
                if (character == null || string.IsNullOrWhiteSpace(character.CharacterName)) continue;

                var confident = character.MatchConfident
                    || (keep.TryGetCharacter(character.CharacterName, out var have) && have.MatchConfident);
                keep.AddCharacter(character.CharacterName, confident);
            }
        }

        private static string Preferred(string first, string second)
            => string.IsNullOrWhiteSpace(first) ? second : first;

        private static bool Matches(PlayerInfo p, PlayerDataQuery q)
        {
            static bool Wild(string want) => string.IsNullOrWhiteSpace(want);

            return (Wild(q.Platform) || p.Platform == q.Platform)
                && (Wild(q.PlayerId) || p.PlayerId == q.PlayerId)
                && (Wild(q.PlayerName) || p.PlayerName == q.PlayerName)
                && (Wild(q.ZdoId) || p.ZdoId == q.ZdoId)
                && (Wild(q.CharacterName) || p.Characters?.Any(c => SameCharacter(c.CharacterName, q.CharacterName)) == true);
        }

        /// <summary>
        /// Whether a character name on file names the character being asked about. The stored
        /// spelling wins as it always did, and a spelling this machine broke on the way in before
        /// 1.2.0, while the server's output was read in the machine's code page rather than as
        /// UTF-8, names the same character too. Without the second half a player whose name is
        /// not plain ASCII comes back after the fix and matches none of their own history, so
        /// the record they belong to has to be guessed from who else is connecting.
        /// </summary>
        private static bool SameCharacter(string stored, string wanted)
        {
            return stored == wanted || TextRepair.IsDamagedSpellingOf(stored, wanted);
        }

        /// <summary>
        /// True when the record belongs to the given server. A null on either
        /// side matches anything, so pre-multi-server records keep working.
        /// </summary>
        private static bool SameServer(PlayerInfo p, string serverKey)
        {
            return string.IsNullOrWhiteSpace(serverKey)
                || string.IsNullOrWhiteSpace(p.ServerKey)
                || string.Equals(p.ServerKey, serverKey, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Decides which player record a freshly spawned character belongs to.
        /// Returns (null, _) when the identity is too ambiguous to call.
        /// </summary>
        private (PlayerInfo player, bool confident) ResolveIdentity(
            string characterName, List<PlayerInfo> byName, List<PlayerInfo> joining)
        {
            if (byName.Count == 1)
            {
                Logger.Information("Character {name} resolved to {key} (only identity seen with that name)",
                    characterName, byName[0].Key);
                return (byName[0], true);
            }

            if (byName.Count > 1)
            {
                var joiningByName = byName.Where(p => p.PlayerStatus == PlayerStatus.Joining).ToList();
                if (joiningByName.Count == 1)
                {
                    Logger.Information("Character {name} resolved to {key} (only joining identity with that name)",
                        characterName, joiningByName[0].Key);
                    return (joiningByName[0], true);
                }

                Logger.Information("Character {name} is ambiguous (several identities with that name)", characterName);
                return (null, false);
            }

            if (joining.Count == 1)
            {
                Logger.Information("Character {name} resolved to {key} (only player joining)",
                    characterName, joining[0].Key);
                return (joining[0], true);
            }

            if (joining.Count > 1)
            {
                // Several first-time characters arriving at once (common on a
                // fresh server): guess the earliest joiner and remember the
                // pairing as low-confidence.
                var guess = joining.OrderBy(p => p.LastStatusChange).First();
                Logger.Information("Character {name} guessed as {key} (several players joining, none seen with that name)",
                    characterName, guess.Key);
                return (guess, false);
            }

            Logger.Information("Character {name} could not be matched to any player", characterName);
            return (null, false);
        }

        private void TransitionAll(PlayerDataQuery query, Func<PlayerInfo, bool> eligible, PlayerStatus status, string verb, string serverKey = null)
        {
            var players = FindPlayersByQuery(query)
                .Where(p => eligible(p) && SameServer(p, serverKey))
                .ToList();
            if (players.Count == 0) return;

            var now = DateTime.UtcNow;
            foreach (var player in players)
            {
                player.PlayerStatus = status;
                player.LastStatusChange = now;
                player.ZdoId = null;
            }

            UpsertBulk(players);
            Logger.Information("{count} player(s) {verb} as: {query}", players.Count, verb, query);
        }

        private static PlayerInfo BuildPlayer(PlayerDataQuery query)
        {
            // Outer clauses win, so apply the Or-chain deepest-first.
            var clauses = new Stack<PlayerDataQuery>();
            for (var q = query; q != null; q = q.Or)
            {
                clauses.Push(q);
            }

            var player = new PlayerInfo();
            while (clauses.Count > 0)
            {
                var clause = clauses.Pop();
                if (!string.IsNullOrWhiteSpace(clause.Platform)) player.Platform = clause.Platform;
                if (!string.IsNullOrWhiteSpace(clause.PlayerId)) player.PlayerId = clause.PlayerId;
                if (!string.IsNullOrWhiteSpace(clause.PlayerName)) player.PlayerName = clause.PlayerName;
                if (!string.IsNullOrWhiteSpace(clause.ZdoId)) player.ZdoId = clause.ZdoId;
                if (!string.IsNullOrWhiteSpace(clause.CharacterName)) player.AddCharacter(clause.CharacterName);
            }

            return player;
        }

        private void TrackStatusTransition(object sender, PlayerInfo player)
        {
            var changed = !KnownStatuses.TryGetValue(player.Key, out var previous)
                || previous != player.PlayerStatus;

            KnownStatuses[player.Key] = player.PlayerStatus;

            if (changed)
            {
                PlayerStatusChanged?.Invoke(this, player);
            }
        }

        private void OnPlayerInfoAvailable(object sender, PlayerInfoResponse response)
        {
            var incomplete = string.IsNullOrWhiteSpace(response.Id)
                || string.IsNullOrWhiteSpace(response.Name)
                || string.IsNullOrWhiteSpace(response.Platform);
            if (incomplete) return;

            var players = FindPlayersByQuery(new()
            {
                Platform = response.Platform,
                PlayerId = response.Id,
            }).ToList();

            foreach (var player in players)
            {
                if (player.PlayerName == response.Name) continue;

                player.PlayerName = response.Name;
                Logger.Information("Player name lookup: {key} is {name}", player.Key, player.PlayerName);
                PlayerStatusChanged?.Invoke(this, player);
            }

            UpsertBulk(players);
        }
    }
}
