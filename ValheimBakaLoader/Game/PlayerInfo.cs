using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using ValheimBakaLoader.Tools;
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

        /// <summary>
        /// The numeric player id Valheim 1.0 prints next to the character name
        /// ("Got player ID from Broheim : 1454938750"). Optional: pre-1.0 servers
        /// never print it and older players.json files have no such field, so it
        /// stays null there and is left out of the file entirely.
        /// </summary>
        [JsonProperty("playerNumericId", NullValueHandling = NullValueHandling.Ignore)]
        public string PlayerNumericId { get; set; }

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

        /// <summary>
        /// Records a character this player has been seen using.
        /// <para>
        /// A row already on file that is THIS character under the spelling the app used to store
        /// it in is taken as this character rather than as another one. Until 1.2.0 the server's
        /// console output was read in the machine's own code page instead of as UTF-8, so a name
        /// outside the first 128 letters went into the file broken; the read is right now, but
        /// the old row is still there, and without this the player comes back and the list grows
        /// a second entry for the one character. The old row is renamed in place, so it keeps its
        /// position, and a further twin of it is dropped. On a machine whose code page cannot be
        /// undone (932, 936, 950) this is the only thing that fixes such a list, which is why it
        /// runs the damage forwards rather than trying to repair what is stored.
        /// </para>
        /// </summary>
        public CharacterInfo AddCharacter(string characterName, bool matchConfident = true)
        {
            Characters ??= new List<CharacterInfo>();

            TryGetCharacter(characterName, out var character);

            for (var i = Characters.Count - 1; i >= 0; i--)
            {
                var row = Characters[i];
                if (row == null || ReferenceEquals(row, character)) continue;
                if (!TextRepair.IsDamagedSpellingOf(row.CharacterName, characterName)) continue;

                if (character == null)
                {
                    row.CharacterName = characterName;
                    character = row;
                }
                else
                {
                    Characters.RemoveAt(i);
                }
            }

            if (character == null)
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
