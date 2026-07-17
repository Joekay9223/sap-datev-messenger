namespace NovaNein.Server;

public sealed record UploadPeriodStatistics(int Total, int Incoming, int Outgoing);

public sealed record RevenueCurrencyStatistics(
	string Currency,
	int InvoiceCount,
	int CreditNoteCount,
	decimal GrossInvoiced,
	decimal GrossCredited,
	decimal NetRevenue);

public sealed record RevenuePeriodStatistics(
	int InvoiceCount,
	int CreditNoteCount,
	IReadOnlyList<RevenueCurrencyStatistics> Currencies);

public sealed record BusinessStatisticsPeriod(
	string Key,
	string Label,
	DateOnly From,
	DateOnly To,
	UploadPeriodStatistics Uploads,
	RevenuePeriodStatistics Revenue);

public sealed record BusinessStatisticsOverview(
	DateTimeOffset GeneratedAt,
	IReadOnlyList<BusinessStatisticsPeriod> Periods);
