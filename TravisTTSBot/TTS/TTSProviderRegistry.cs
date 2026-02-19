namespace DiscordTTSBot.TTS
{
	public class TTSProviderRegistry
	{
		private readonly ITTSProvider _default;
		private readonly Dictionary<ulong, ITTSProvider> _userOverrides = new();
		private readonly Dictionary<ulong, string> _userVoiceOverrides = new();

		public TTSProviderRegistry(ITTSProvider defaultProvider)
		{
			_default = defaultProvider;
		}

		public void SetUserProvider(ulong userId, ITTSProvider provider, string? voice = null)
		{
			_userOverrides[userId] = provider;
			if (voice is not null)
				_userVoiceOverrides[userId] = voice;
		}

		public ITTSProvider GetProviderForUser(ulong userId)
		{
			return _userOverrides.TryGetValue(userId, out var provider)
				? provider
				: _default;
		}

		public string? GetVoiceOverride(ulong userId)
		{
			return _userVoiceOverrides.TryGetValue(userId, out var voice) ? voice : null;
		}
	}
}
