﻿using Newtonsoft.Json;

namespace TrickFireDiscordBot
{
    /// <summary>
    /// A class to store config values in. Do not store secrets like the discord
    /// token inside of this class.
    /// </summary>
    public class Config
    {
        // Use singleton because we can't easily deserialize into a static class
        /// <summary>
        /// The instance of the config object.
        /// </summary>
        public static Config Instance { get; } = LoadConfig("config.json");

        /// <summary>
        /// The full path this config object is saved at and loaded from.
        /// </summary>
        [JsonIgnore]
        public string ConfigPath { get; set; } = "";

        /// <summary>
        /// The id of the trickfire Discord server.
        /// </summary>
        public ulong TrickfireGuildId { get; set; } = 0;

        /// <summary>
        /// The id of the channel that current attendance is sent to.
        /// </summary>
        public ulong CheckInChannelId { get; set; } = 0;

        /// <summary>
        /// The id of the message that has the list of members in the shop.
        /// </summary>
        public ulong ListMessageId { get; set; } = 0;

        /// <summary>
        /// The id of the Teams page database in Notion.
        /// </summary>
        public string TeamsDatabaseId { get; set; } = "";

        /// <summary>
        /// The id of the Members page database in Notion.
        /// </summary>
        public string MembersDatabaseId { get; set; } = "";

        /// <summary>
        /// The name of the database property for a members' discord username.
        /// </summary>
        public string DiscordUsernamePropertyName { get; set; } = "";

        /// <summary>
        /// The name of the database property for a members' active status.
        /// </summary>
        public string ActivePropertyName { get; set; } = "";

        /// <summary>
        /// The name of the database property for a members' club positions.
        /// </summary>
        public string ClubPositionsPropertyName { get; set; } = "";

        /// <summary>
        /// The name of the database property for a members' teams.
        /// </summary>
        public string TeamsPropertyName { get; set; } = "";

        /// <summary>
        /// The name of the database property for a team's name
        /// </summary>
        public string TeamNamePropertyName { get; set; } = "";

        /// <summary>
        /// Returns a config object loaded from the file at <paramref name="path"/>.
        /// </summary>
        /// <param name="path">The path to load the config from</param>
        /// <returns>The deserialized config</returns>
        /// <exception cref="FileNotFoundException">If there is no file at <paramref name="path"/></exception>
        /// <exception cref="ArgumentException">If a config could not be deserialized</exception>
        private static Config LoadConfig(string path)
        {
            // Convert any relative paths to absolute
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "{}");
            }

            // Deserialize file. Throw error if deserialized fails (returns null)
            Config? deserialized = JsonConvert.DeserializeObject<Config>(File.ReadAllText(path)) 
                ?? throw new ArgumentException($"Could not deserialize config file at path: `{path}`");
            deserialized.ConfigPath = path;

            return deserialized;
        }

        /// <summary>
        /// Saves the current config to <see cref="ConfigPath"/>.
        /// </summary>
        public void SaveConfig()
        {
            string serialized = JsonConvert.SerializeObject(this, Formatting.Indented);

            File.WriteAllText(ConfigPath, serialized);
        }
    }
}
