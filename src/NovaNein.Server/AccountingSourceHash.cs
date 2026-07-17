using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaNein.Server;

public static class AccountingSourceHash
{
	public static string Create(SapDocumentSnapshot snapshot, IReadOnlyList<SapDocumentLine> lines, IReadOnlyList<SapTaxBreakdown> taxes, IReadOnlyList<SapJournalLine> journalLines, IReadOnlyList<DatevBookingMapping> mappings)
	{
		string payload = JsonSerializer.Serialize(new { snapshot, lines, taxes, journalLines, mappings });
		return Hash(payload);
	}

	public static string CreateMappingHash(string taxCode, string buCode, string account, DateOnly from, DateOnly? to, string approvedBy)
	{
		return Hash(string.Join("|", taxCode.Trim(), buCode.Trim(), account.Trim(), from.ToString("yyyy-MM-dd"), to?.ToString("yyyy-MM-dd") ?? string.Empty, approvedBy.Trim()));
	}

	private static string Hash(string value)
	{
		byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
		return Convert.ToHexString(bytes);
	}
}
