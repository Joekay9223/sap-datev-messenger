using System.Globalization;
using System.Text;

namespace NovaNein.Server;

public static class InventoryGoodsClassifier
{
	private static readonly string[] InventoryTerms =
	[
		"inventory item",
		"stocked item",
		"raw material",
		"raw materials",
		"rohstoff",
		"rohstoffe",
		"verpackungsmaterial",
		"verpackung",
		"packaging",
		"karton",
		"faltschachtel",
		"beutel",
		"pouch",
		"sack",
		"folie",
		"film roll",
		"etikett",
		"label",
		"eimer",
		"dose",
		"flasche",
		"deckel",
		"verschluss",
		"palette",
		"paletten",
		"tray",
		"displaykarton"
	];

	public static bool RequiresItemPosting(string? description)
	{
		var normalized = Normalize(description);
		return normalized.Length > 0 && InventoryTerms.Any(normalized.Contains);
	}

	public static ExtractedInvoiceFacts Apply(ExtractedInvoiceFacts facts)
	{
		var sourceLines = facts.Lines ?? [];
		var lines = sourceLines
			.Select(line => line with { LooksLikeGoods = RequiresItemPosting(line.Description) })
			.ToArray();
		var requiresItemPosting = lines.Any(line => line.LooksLikeGoods)
			|| (lines.Length == 0 && RequiresItemPosting(facts.Text));
		return facts with
		{
			Lines = lines,
			HasGoodsCharacteristics = requiresItemPosting,
			HasPurchaseOrderReference = requiresItemPosting && facts.HasPurchaseOrderReference
		};
	}

	private static string Normalize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return string.Empty;
		var decomposed = value.Normalize(NormalizationForm.FormD);
		var builder = new StringBuilder(decomposed.Length);
		foreach (var character in decomposed)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
				continue;
			builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
		}
		return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
	}
}
