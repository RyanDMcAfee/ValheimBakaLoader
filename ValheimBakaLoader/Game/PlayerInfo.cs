using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using ValheimBakaLoader.Tools.Data;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// One known player, identified by (platform, platform-id). The same human
    /// may play several characters but keeps a single entry here. Persisted in
    /// players.json; the JSON key names are a compatibility contract.
    /// </summary>
    public class PlayerInfo : IKeyed
    {
        [JsonIgnore] public string Key => $"{Platform}:{PlayerId}";

        [JsonProperty("platform")] public string Platform { get; set; }
        [JsonProperty("playerId")] public string PlayerId { get; set; }
        [JsonProperty("playerName")] public string PlayerName { get; set; }

        /// <summary>When the player's status last flipped (join/leave/etc).</summary>
        [JsonProperty("lastStatusChange")] public DateTimeOffset LastStatusChange { get; set; }

        [JsonProperty("lastStatusCharacter")] public string LastStatusCharacter { get; set; }

        /// <summary>Every character this player has been seen using.</summary>
        [JsonProperty("characters")] public List<CharacterInfo> Characters { get; set; }

        /// <summary>
        /// The server profile this player was last seen on. Null on records
        /// written before multi-server support; treated as matching any server.
        /// </summary>
        [JsonProperty("serverKey")] public string ServerKey { get; set; }

        // -- Runtime-only session state, never persisted --

        [JsonIgnore] public PlayerStatus PlayerStatus { get; set; }

        /// <summary>In-game object id; changes every session.</summary>
        [JsonIgnore] public string ZdoId { get; set; }

        public CharacterInfo AddCharacter(string characterName, bool matchConfident = true)
        {
            Characters ??= new List<CharacterInfo>();

            if (!TryGetCharacter(characterName, out var character))
            {
                character = new CharacterInfo { CharacterName = characterName };
                Characters.Add(character);
            }

            character.MatchConfident = matchConfident;
            return character;
        }

        public bool TryGetCharacter(string characterName, out CharacterInfo character)
        {
            character = Characters?.Find(c => c.CharacterName == characterName);
            return character != null;
        }

        public class CharacterInfo
        {
            [JsonProperty("characterName")] public string CharacterName { get; set; }

            /// <summary>
            /// False when the name-to-platform-id pairing was inferred from log
            /// ordering rather than observed directly.
            /// </summary>
            [JsonProperty("matchConfident")] public bool MatchConfident { get; set; }
        }
    }
}
