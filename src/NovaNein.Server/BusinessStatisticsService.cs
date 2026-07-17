namespace NovaNein.Server;

public sealed class BusinessStatisticsService(DocumentStore documents, ISapServiceLayerClient sap)
{
	private sealed record PeriodDefinition(string Key, string Label, DateOnly From, DateOnly To);

	public Task<BusinessStatisticsOverview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
		GetOverviewAsync(DateOnly.FromDateTime(DateTime.Today), cancellationToken);

	internal async Task<BusinessStatisticsOverview> GetOverviewAsync(DateOnly today, CancellationToken cancellationToken = default)
	{
		PeriodDefinition[] definitions =
		[
			new("today", "Heute", today, today),
			new("yesterday", "Gestern", today.AddDays(-1), today.AddDays(-1)),
			new("last7", "Letzte 7 Tage", today.AddDays(-6), today),
			new("last30", "Letzte 30 Tage", today.AddDays(-29), today)
		];

		DateOnly earliest = definitions.Min(period => period.From);
		IReadOnlyList<SapDocumentSnapshot> invoices = await sap.ListDocumentsAsync(
			SapDocumentKind.Invoice,
			earliest,
			today,
			cancellationToken);
		IReadOnlyList<SapDocumentSnapshot> creditNotes = await sap.ListDocumentsAsync(
			SapDocumentKind.CreditNote,
			earliest,
			today,
			cancellationToken);

		List<BusinessStatisticsPeriod> periods = [];
		foreach (PeriodDefinition definition in definitions)
		{
			UploadPeriodStatistics uploads = await documents.UploadStatisticsAsync(
				definition.From,
				definition.To,
				cancellationToken);
			RevenuePeriodStatistics revenue = BuildRevenue(definition, invoices, creditNotes);
			periods.Add(new BusinessStatisticsPeriod(
				definition.Key,
				definition.Label,
				definition.From,
				definition.To,
				uploads,
				revenue));
		}

		return new BusinessStatisticsOverview(DateTimeOffset.UtcNow, periods);
	}

	private static RevenuePeriodStatistics BuildRevenue(
		PeriodDefinition period,
		IReadOnlyList<SapDocumentSnapshot> allInvoices,
		IReadOnlyList<SapDocumentSnapshot> allCreditNotes)
	{
		SapDocumentSnapshot[] invoices = allInvoices.Where(document => IsInPeriod(document, period)).ToArray();
		SapDocumentSnapshot[] creditNotes = allCreditNotes.Where(document => IsInPeriod(document, period)).ToArray();
		string[] currencies = invoices.Concat(creditNotes)
			.Select(document => string.IsNullOrWhiteSpace(document.Currency) ? "EUR" : document.Currency.Trim().ToUpperInvariant())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(currency => currency, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		RevenueCurrencyStatistics[] totals = currencies.Select(currency =>
		{
			SapDocumentSnapshot[] currencyInvoices = invoices.Where(document => HasCurrency(document, currency)).ToArray();
			SapDocumentSnapshot[] currencyCreditNotes = creditNotes.Where(document => HasCurrency(document, currency)).ToArray();
			decimal grossInvoiced = decimal.Round(currencyInvoices.Sum(document => Math.Abs(document.GrossAmount)), 2, MidpointRounding.AwayFromZero);
			decimal grossCredited = decimal.Round(currencyCreditNotes.Sum(document => Math.Abs(document.GrossAmount)), 2, MidpointRounding.AwayFromZero);
			return new RevenueCurrencyStatistics(
				currency,
				currencyInvoices.Length,
				currencyCreditNotes.Length,
				grossInvoiced,
				grossCredited,
				decimal.Round(grossInvoiced - grossCredited, 2, MidpointRounding.AwayFromZero));
		}).ToArray();

		return new RevenuePeriodStatistics(invoices.Length, creditNotes.Length, totals);
	}

	private static bool IsInPeriod(SapDocumentSnapshot document, PeriodDefinition period)
	{
		DateOnly date = document.EntryDate ?? document.DocumentDate;
		return date >= period.From && date <= period.To;
	}

	private static bool HasCurrency(SapDocumentSnapshot document, string currency)
	{
		string normalized = string.IsNullOrWhiteSpace(document.Currency) ? "EUR" : document.Currency.Trim();
		return string.Equals(normalized, currency, StringComparison.OrdinalIgnoreCase);
	}
}
