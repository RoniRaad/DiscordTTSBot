using System.Net.Http.Json;

namespace DiscordTTSBot
{
	public class ServerReservationClient
	{
		private readonly HttpClient _httpClient;
		private readonly string _baseUrl;
		private readonly string _apiKey;
		private string? _reservationId;

		public ServerReservationClient(string baseUrl, string apiKey)
		{
			_baseUrl = baseUrl.TrimEnd('/');
			_apiKey = apiKey;
			_httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
			_httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
		}

		/// <summary>
		/// Creates a reservation and waits for the server to come online.
		/// Stores the reservation ID for later renewal/release.
		/// </summary>
		public async Task ReserveAsync(int durationMinutes = 60, bool wait = true)
		{
			Console.WriteLine($"[Reservation] Creating reservation (duration: {durationMinutes}m, wait: {wait})...");

			var response = await _httpClient.PostAsJsonAsync(
				$"{_baseUrl}/api/reservations?wait={wait.ToString().ToLower()}",
				new
				{
					clientName = "discord-tts-bot",
					purpose = "TTS voice synthesis",
					durationMinutes
				});

			response.EnsureSuccessStatusCode();

			var result = await response.Content.ReadFromJsonAsync<ReservationResponse>();
			_reservationId = result?.Id;

			Console.WriteLine($"[Reservation] Server reserved (id: {_reservationId}, expires: {result?.ExpiresAt})");
		}

		/// <summary>
		/// Renews the current reservation.
		/// </summary>
		public async Task RenewAsync(int extendByMinutes = 60)
		{
			if (_reservationId is null)
			{
				await ReserveAsync(extendByMinutes);
				return;
			}

			try
			{
				var response = await _httpClient.PostAsJsonAsync(
					$"{_baseUrl}/api/reservations/{_reservationId}/renew",
					new { extendByMinutes });

				if (response.IsSuccessStatusCode)
				{
					var result = await response.Content.ReadFromJsonAsync<ReservationResponse>();
					Console.WriteLine($"[Reservation] Renewed (expires: {result?.ExpiresAt})");
				}
				else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
				{
					// Reservation expired, create a new one
					Console.WriteLine("[Reservation] Previous reservation expired, creating new one...");
					_reservationId = null;
					await ReserveAsync(extendByMinutes);
				}
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[Reservation] Renewal failed: {ex.Message}");
			}
		}

		/// <summary>
		/// Releases the current reservation, allowing the server to shut down
		/// after the grace period if no other reservations are active.
		/// </summary>
		public async Task ReleaseAsync()
		{
			if (_reservationId is null)
				return;

			try
			{
				var response = await _httpClient.DeleteAsync($"{_baseUrl}/api/reservations/{_reservationId}");
				Console.WriteLine($"[Reservation] Released (id: {_reservationId})");
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[Reservation] Release failed: {ex.Message}");
			}

			_reservationId = null;
		}

		public bool HasActiveReservation => _reservationId is not null;

		private record ReservationResponse(string Id, string ClientName, string? Purpose, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, double RemainingSeconds);
	}
}
