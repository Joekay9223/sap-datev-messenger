using System;
using System.Security.Cryptography;

namespace NovaNein.Server;

public static class WebCsrf
{
	public const string CookieName = "NovaNein.Csrf";

	public const string HeaderName = "X-CSRF-Token";

	public static string CreateToken()
	{
		return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
	}

	public static bool Matches(string? expected, string? supplied)
	{
		if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
		{
			return false;
		}
		try
		{
			return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(expected), Convert.FromBase64String(supplied));
		}
		catch (FormatException)
		{
			return false;
		}
	}
}
