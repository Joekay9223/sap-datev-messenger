namespace NovaNein.Server;

public enum ReconciliationStatus
{
	Matched,
	InSapNotInDatev,
	InDatevNotInSap,
	AmountOrCurrencyMismatch,
	Ambiguous,
	PdfMissing,
	ManuallyDecided
}
