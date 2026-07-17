namespace NovaNein.Server;

public sealed record SapTaxBreakdown(
	int LineNum,
	string TaxCode,
	decimal Rate,
	decimal NetAmount,
	decimal TaxAmount,
	string Currency,
	string TaxAccount = "",
	decimal ReverseChargePercent = 0m,
	decimal ReverseChargeTaxAmount = 0m,
	bool IncludedInGrossRevenue = false)
{
	public bool IsReverseCharge => ReverseChargePercent != 0m || ReverseChargeTaxAmount != 0m;
}
