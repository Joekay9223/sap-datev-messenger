using System;
using System.Net;
using System.Net.Sockets;

namespace NovaNein.Server;

public readonly record struct CidrNetwork(IPAddress Network, int PrefixLength)
{
	public static CidrNetwork Parse(string value)
	{
		string[] parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
		if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress address) || !int.TryParse(parts[1], out var prefix))
		{
			throw new InvalidOperationException("Ungültiges Netzwerk in WebAccess:AllowedCidrs: " + value);
		}
		if (address.IsIPv4MappedToIPv6)
		{
			address = address.MapToIPv4();
		}
		int maximum = ((address.AddressFamily == AddressFamily.InterNetwork) ? 32 : 128);
		if (prefix < 0 || prefix > maximum)
		{
			throw new InvalidOperationException("Ungültige Präfixlänge in WebAccess:AllowedCidrs: " + value);
		}
		return new CidrNetwork(address, prefix);
	}

	public bool Contains(IPAddress address)
	{
		if (address.IsIPv4MappedToIPv6)
		{
			address = address.MapToIPv4();
		}
		if (address.AddressFamily != Network.AddressFamily)
		{
			return false;
		}
		byte[] candidate = address.GetAddressBytes();
		byte[] network = Network.GetAddressBytes();
		int completeBytes = PrefixLength / 8;
		int remainingBits = PrefixLength % 8;
		for (int i = 0; i < completeBytes; i++)
		{
			if (candidate[i] != network[i])
			{
				return false;
			}
		}
		if (remainingBits == 0)
		{
			return true;
		}
		byte mask = (byte)(255 << 8 - remainingBits);
		return (candidate[completeBytes] & mask) == (network[completeBytes] & mask);
	}
}
