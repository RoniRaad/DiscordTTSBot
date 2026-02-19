using System.Net;
using System.Net.Sockets;

namespace DiscordTTSBot.Static
{
	public static class WakeOnLan
	{
		public static async Task SendAsync(string macAddress, string? broadcastIp = null)
		{
			var mac = macAddress.Replace(":", "").Replace("-", "");
			if (mac.Length != 12)
				throw new ArgumentException("Invalid MAC address");

			var macBytes = Enumerable.Range(0, 6)
				.Select(i => Convert.ToByte(mac.Substring(i * 2, 2), 16))
				.ToArray();

			// Magic packet: 6x 0xFF + 16x MAC
			var packet = new byte[102];
			for (var i = 0; i < 6; i++)
				packet[i] = 0xFF;
			for (var i = 0; i < 16; i++)
				Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);

			var broadcastAddress = broadcastIp is not null
				? IPAddress.Parse(broadcastIp)
				: IPAddress.Broadcast;

			using var udp = new UdpClient();
			udp.EnableBroadcast = true;

			// Send on both common WOL ports
			await udp.SendAsync(packet, packet.Length, new IPEndPoint(broadcastAddress, 7));
			await udp.SendAsync(packet, packet.Length, new IPEndPoint(broadcastAddress, 9));

			Console.WriteLine($"WOL packet sent to {macAddress} via {broadcastAddress}");
		}
	}
}
