namespace NovaNein.Server;

public sealed record SapDocumentLine(
	int LineNum,
	string Description,
	decimal Quantity,
	decimal NetAmount,
	decimal TaxAmount,
	string TaxCode,
	decimal TaxRate,
	string Account,
	string Currency,
	bool IsReverseCharge = false);
