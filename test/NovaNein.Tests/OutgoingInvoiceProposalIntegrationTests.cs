using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class OutgoingInvoiceProposalIntegrationTests : IAsyncLifetime
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "novanein-outgoing-" + Guid.NewGuid().ToString("N"));

	public Task InitializeAsync()
	{
		Directory.CreateDirectory(_root);
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_root)) Directory.Delete(_root, true);
		return Task.CompletedTask;
	}

	[Fact]
	public async Task Forwarded_existing_outgoing_invoice_becomes_green_with_sap_coding_without_manual_edits()
	{
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Storage:DatabasePath"] = Path.Combine(_root, "novanein.db"),
			["Storage:DocumentRoot"] = _root,
			["Company:VatId"] = "DE000000000",
			["Company:Names:0"] = "demo-company"
		}).Build();
		var store = new AutomaticBookingStore(configuration);
		await store.InitializeAsync();
		var pdf = Path.Combine(_root, "Ausgangsrechnung-900003.pdf");
		await File.WriteAllTextAsync(pdf, "%PDF-test");
		var mail = await store.CreateMailAsync(
			"invoices@example.invalid", "message-1", "thread-1", "history-1", "WG: Rechnung 900003", "maintainer@example.test",
			DateTimeOffset.UtcNow,
			[new MailAttachmentRecord(Guid.NewGuid(), Guid.Empty, "attachment-1", Path.GetFileName(pdf), "application/pdf", 9, new string('A', 64), pdf, "Ready", null)]);
		var attachment = Assert.Single(mail.Attachments!);
		var service = new InvoiceProposalService(
			store,
			new OutgoingSap(),
			new OutgoingExtractor(),
			new DocumentStore(configuration),
			new DocumentJobQueue(configuration),
			new NoGmail(),
			configuration,
			NullLogger<InvoiceProposalService>.Instance);

		var proposal = await service.CreateOrRecalculateAsync(mail, attachment, null);

		Assert.Equal("outgoing", proposal.Direction);
		Assert.Equal(MailSourceStatuses.ProposalReady, proposal.Status);
		Assert.Equal("green", proposal.Signal);
		Assert.True(proposal.HasGoodsCharacteristics);
		var line = Assert.Single(proposal.Lines);
		Assert.Equal("8400", line.Account);
		Assert.Equal("A2", line.TaxCode);
		Assert.Equal("SAP-Readback", line.SuggestionSource);
	}

	private sealed class OutgoingExtractor : IPdfInvoiceTextExtractor
	{
		public ExtractedInvoiceFacts Extract(string path, NovaNein.Domain.DocumentDirection? direction = null) => new(
			"900003", "Example Customer GmbH", "DE999", 119m, "EUR", new DateOnly(2026, 7, 17),
			true, false, false, "demo-company Ausgangsrechnung 900003 an Example Customer GmbH, Inventory item",
			NetAmount: 100m, TaxAmount: 19m,
			IssuerName: "Example Company GmbH", IssuerVatId: "DE000000000",
			RecipientName: "Example Customer GmbH", RecipientVatId: "DE999",
			Lines: [new ExtractedInvoiceLine(1, "Inventory item", 100m, 19m, 19m, null, null, true)],
			HasGoodsCharacteristics: true);
	}

	private sealed class OutgoingSap : ISapServiceLayerClient
	{
		private static readonly SapDocumentSnapshot Snapshot = new(
			SapDocumentKind.Invoice, 4711, 900003, "C100", "Example Customer GmbH", "900003",
			new DateOnly(2026, 7, 17), 119m, "EUR", null, TransId: 9001);

		public Task<SapDocumentSnapshot?> FindDocumentByDocNumAsync(SapDocumentKind kind, int docNum, CancellationToken cancellationToken = default) =>
			Task.FromResult<SapDocumentSnapshot?>(kind == SapDocumentKind.Invoice && docNum == 900003 ? Snapshot : null);

		public Task<SapAccountingDocument?> GetAccountingDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default) =>
			Task.FromResult<SapAccountingDocument?>(new SapAccountingDocument(
				Snapshot, 9001, "10001", "DE999", "Kundenweg 1", "20095", "Hamburg",
				"Example Company GmbH", "DE000000000", "Example Street 1", "12345", "Example City",
				[new SapDocumentLine(0, "Inventory item", 1m, 100m, 19m, "A2", 19m, "8400", "EUR")],
				[new SapTaxBreakdown(0, "A2", 19m, 100m, 19m, "EUR")],
				[
					new SapJournalLine(0, "10001", "8400", "S", 119m, 0m, ""),
					new SapJournalLine(1, "8400", "10001", "H", 0m, 100m, ""),
					new SapJournalLine(2, "1776", "10001", "H", 0m, 19m, "")
				],
				[new DatevBookingMapping("A2", "3", "8400", new DateOnly(2026, 1, 1), null, "test", "hash")],
				new string('B', 64)));

		public Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
		public Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SapAttachmentGap>>([]);
		public Task CheckReadinessAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task AttachPdfAsync(SapDocumentKind kind, int docEntry, int expectedDocNum, string localPdfPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class NoGmail : IGmailApiClient
	{
		public bool IsConfigured => false;
		public Task<IReadOnlyList<string>> ListMessageIdsAsync(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<(IReadOnlyList<string> MessageIds, string? LatestHistoryId)> ListHistoryAsync(string startHistoryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GmailMessageEnvelope> GetMessageAsync(string messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<byte[]> DownloadAttachmentAsync(string messageId, GmailAttachmentDescriptor attachment, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<string, string>> EnsureLabelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task ModifyLabelsAsync(string messageId, IEnumerable<string> addLabelIds, IEnumerable<string>? removeLabelIds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<(string HistoryId, DateTimeOffset Expiration)> RenewWatchAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> PullNotificationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
