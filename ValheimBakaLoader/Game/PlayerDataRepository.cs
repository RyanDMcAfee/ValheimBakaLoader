using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
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

        private static bool Matches(PlayerInfo p, PlayerDataQuery q)
        {
            static bool Wild(string want) => string.IsNullOrWhiteSpace(want);

            return (Wild(q.Platform) || p.Platform == q.Platform)
                && (Wild(q.PlayerId) || p.PlayerId == q.PlayerId)
                && (Wild(q.PlayerName) || p.PlayerName == q.PlayerName)
                && (Wild(q.ZdoId) || p.ZdoId == q.ZdoId)
                && (Wild(q.CharacterName) || p.Characters?.Any(c => c.CharacterName == q.CharacterName) == true);
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
