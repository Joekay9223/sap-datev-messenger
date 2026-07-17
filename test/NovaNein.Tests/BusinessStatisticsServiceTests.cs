using Microsoft.Extensions.Configuration;
using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class BusinessStatisticsServiceTests : IAsyncLifetime
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-statistics-{Guid.NewGuid():N}");
	private DocumentStore _documents = null!;

	public async Task InitializeAsync()
	{
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Storage:DatabasePath"] = Path.Combine(_directory, "archive.db")
		}).Build();
		_documents = new DocumentStore(configuration);
		await _documents.InitializeAsync();
	}

	public Task DisposeAsync()
	{
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
		return Task.CompletedTask;
	}

	[Fact]
	public async Task Builds_upload_and_net_revenue_periods_without_mixing_currencies()
	{
		DateOnly today = DateOnly.FromDateTime(DateTime.Today);
		await _documents.CreateAsync(new(DocumentDirection.Incoming, 1, 500001), "1".PadLeft(64, '1'), "incoming.pdf", "tester");
		await _documents.CreateAsync(new(DocumentDirection.Outgoing, 2, 110001), "2".PadLeft(64, '2'), "outgoing.pdf", "tester");

		FakeSap sap = new(
		[
			Snapshot(SapDocumentKind.Invoice, 1, today, 100m, "EUR"),
			Snapshot(SapDocumentKind.Invoice, 2, today.AddDays(-1), 200m, "EUR"),
			Snapshot(SapDocumentKind.Invoice, 3, today.AddDays(-10), 300m, "EUR"),
			Snapshot(SapDocumentKind.Invoice, 4, today, 50m, "USD"),
			Snapshot(SapDocumentKind.Invoice, 5, today.AddDays(-35), 999m, "EUR")
		],
		[
			Snapshot(SapDocumentKind.CreditNote, 6, today, 20m, "EUR"),
			Snapshot(SapDocumentKind.CreditNote, 7, today.AddDays(-1), 50m, "EUR")
		]);
		BusinessStatisticsOverview result = await new BusinessStatisticsService(_documents, sap).GetOverviewAsync(today);

		Assert.Equal(["today", "yesterday", "last7", "last30"], result.Periods.Select(period => period.Key));
		BusinessStatisticsPeriod current = result.Periods.Single(period => period.Key == "today");
		Assert.Equal(2, current.Uploads.Total);
		Assert.Equal(1, current.Uploads.Incoming);
		Assert.Equal(1, current.Uploads.Outgoing);
		Assert.Equal(2, current.Revenue.InvoiceCount);
		Assert.Equal(1, current.Revenue.CreditNoteCount);
		Assert.Equal(80m, current.Revenue.Currencies.Single(value => value.Currency == "EUR").NetRevenue);
		Assert.Equal(50m, current.Revenue.Currencies.Single(value => value.Currency == "USD").NetRevenue);

		BusinessStatisticsPeriod last30 = result.Periods.Single(period => period.Key == "last30");
		Assert.Equal(530m, last30.Revenue.Currencies.Single(value => value.Currency == "EUR").NetRevenue);
		Assert.DoesNotContain(last30.Revenue.Currencies, value => value.NetRevenue == 999m);
	}

	private static SapDocumentSnapshot Snapshot(SapDocumentKind kind, int id, DateOnly entryDate, decimal amount, string currency) =>
		new(kind, id, 100000 + id, "C1", "Testkunde", (100000 + id).ToString(), entryDate, amount, currency, null, entryDate);

	private sealed class FakeSap(IReadOnlyList<SapDocumentSnapshot> invoices, IReadOnlyList<SapDocumentSnapshot> creditNotes) : ISapServiceLayerClient
	{
		public Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<SapDocumentSnapshot>> ListDocumentsAsync(SapDocumentKind kind, DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default)
		{
			IReadOnlyList<SapDocumentSnapshot> source = kind == SapDocumentKind.Invoice ? invoices : creditNotes;
			return Task.FromResult<IReadOnlyList<SapDocumentSnapshot>>(source.Where(document =>
			{
				DateOnly date = document.EntryDate ?? document.DocumentDate;
				return date >= fromEntryDate && date <= toEntryDate;
			}).ToArray());
		}

		public Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<SapAttachmentGap>>([]);

		public Task CheckReadinessAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

		public Task AttachPdfAsync(SapDocumentKind kind, int docEntry, int expectedDocNum, string localPdfPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}
}
