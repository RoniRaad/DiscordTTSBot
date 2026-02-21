using System.Diagnostics;

namespace TTSBot.Static
{
    public static class StreamHelpers
    {
		public static async Task<Stream> ConvertToDiscordAudioFormat(Stream inputStream, CancellationToken cancellationToken = default)
		{
			var outputMemoryStream = new MemoryStream();

			var processStartInfo = new ProcessStartInfo
			{
				FileName = "ffmpeg",
				Arguments = "-loglevel quiet -i pipe:0 -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = false,
			};

			using (var process = new Process { StartInfo = processStartInfo })
			{
				process.Start();

				using var reg = cancellationToken.Register(() =>
				{
					try { process.Kill(); } catch { }
				});

				var inputTask = inputStream.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
				var closeTask = inputTask.ContinueWith(task => process.StandardInput.Close(), CancellationToken.None);

				var outputTask = process.StandardOutput.BaseStream.CopyToAsync(outputMemoryStream, cancellationToken);

				await Task.WhenAll(process.WaitForExitAsync(cancellationToken), inputTask, outputTask, closeTask);

				cancellationToken.ThrowIfCancellationRequested();

				outputMemoryStream.Position = 0;

				return outputMemoryStream;
			}
		}

		/// <summary>
		/// Pipes an audio stream through FFmpeg and copies the PCM output directly
		/// to the destination stream (e.g. an OpusEncodeStream) as data arrives.
		/// </summary>
		public static async Task StreamToDiscordAudioFormat(Stream inputStream, Stream outputStream, CancellationToken cancellationToken = default)
		{
			var processStartInfo = new ProcessStartInfo
			{
				FileName = "ffmpeg",
				Arguments = "-loglevel quiet -i pipe:0 -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = false,
			};

			using var process = new Process { StartInfo = processStartInfo };
			process.Start();

			using var reg = cancellationToken.Register(() =>
			{
				try { process.Kill(); } catch { }
			});

			var inputTask = inputStream.CopyToAsync(process.StandardInput.BaseStream, cancellationToken)
				.ContinueWith(_ => process.StandardInput.Close(), CancellationToken.None);

			var outputTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream, cancellationToken);

			await Task.WhenAll(process.WaitForExitAsync(cancellationToken), inputTask, outputTask);

			cancellationToken.ThrowIfCancellationRequested();
		}
    }
}
