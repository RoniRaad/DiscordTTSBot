using System.Text;
using System.Text.Json;
using DiscordTTSBot.Models;

namespace DiscordTTSBot.Static
{
    public static class UserSettingsHelper
    {
        public const string ConfigFilePath = "usersConfig.json";
		public static async Task SetUserVoice(ulong id, string voiceSetting)
        {
            var userSettings = GetUserSettings(id);

            userSettings.Voice = voiceSetting;

            await SetUserSettings(id, userSettings);
        }

        public static UserSettings GetUserSettings(ulong id)
        {
            Dictionary<ulong, UserSettings>? settings;
            UserSettings? userSettings;

            if (!File.Exists(ConfigFilePath))
            {
                return new UserSettings();
            }
            else
            {
                settings = JsonSerializer.Deserialize<Dictionary<ulong, UserSettings>>(File.ReadAllText(ConfigFilePath));
                if (settings is null)
                    return new();

				if (!settings.TryGetValue(id, out userSettings))
                {
                    userSettings = new UserSettings();
                }
            }

            return userSettings;
        }

        internal static string GetUserVoice(ulong id)
        {
            return GetUserSettings(id).Voice;
        }

        public static async Task<UserSettings> SetUserSettings(ulong id, UserSettings userSettings)
        {
            Dictionary<ulong, UserSettings>? settings;

            if (!File.Exists(ConfigFilePath))
            {
                settings = new()
                {
                    {
                        id,
                        userSettings
                    }
                };
                var fileContext = File.Create(ConfigFilePath);
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings));
                fileContext.Write(bytes, 0, bytes.Length);
                fileContext.Close();
            }
            else
            {
                settings = JsonSerializer.Deserialize<Dictionary<ulong, UserSettings>>(await File.ReadAllTextAsync(ConfigFilePath));
                if (settings is null)
                    settings = new();

                settings[id] = userSettings;
                await File.WriteAllTextAsync(ConfigFilePath, JsonSerializer.Serialize(settings));
            }

            return userSettings;
        }
    }
}
