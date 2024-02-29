using System.Diagnostics;

namespace TTSBot.Static
{
    public static class StreamHelpers
    {
		public static async Task<Stream> ConvertToDiscordAudioFormat(Stream inputStream)
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

				var inputTask = inputStream.CopyToAsync(process.StandardInput.BaseStream);
				var closeTask = inputTask.ContinueWith(task => process.StandardInput.Close());

				var outputTask = process.StandardOutput.BaseStream.CopyToAsync(outputMemoryStream);

				await Task.WhenAll(process.WaitForExitAsync(), inputTask, outputTask, closeTask);

				outputMemoryStream.Position = 0;

				return outputMemoryStream;
			}
		}
    }
}
