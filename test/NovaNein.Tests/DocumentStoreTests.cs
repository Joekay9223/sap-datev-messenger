using Microsoft.Extensions.Configuration;
using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class DocumentStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-tests-{Guid.NewGuid():N}");
    private DocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DatabasePath"] = Path.Combine(_directory, "archive.db")
        }).Build();
        _store = new DocumentStore(configuration);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }

    [Fact]
    public async Task Yellow_document_requires_reasoned_single_manual_review()
    {
        var item = await _store.CreateAsync(new(DocumentDirection.Incoming, 10, 20), "A".PadLeft(64, 'A'), "invoice.pdf", "tester");
        var validated = await _store.RecordValidationAsync(item.Id, new(ReviewSignal.Yellow, ["OCR unsicher"]), "engine");
        Assert.Equal(DocumentStatus.NeedsReview, validated!.Status);

        await Assert.ThrowsAsync<ArgumentException>(() => _store.ReviewAsync(item.Id, true, " ", "reviewer"));
        var approved = await _store.ReviewAsync(item.Id, true, "OCR manuell gegen Original geprüft.", "reviewer");
        Assert.Equal(DocumentStatus.Approved, approved!.Status);
        Assert.Null(await _store.ReviewAsync(item.Id, true, "zweiter Versuch", "reviewer"));
        Assert.Contains((await _store.EventsAsync(item.Id)).Select(x => x.Kind), x => x == "ManualReviewApproved");
    }

    [Fact]
    public async Task Red_document_can_be_manually_approved_once_with_a_reason()
    {
        var item = await _store.CreateAsync(new(DocumentDirection.Incoming, 11, 21), "B".PadLeft(64, 'B'), "invoice.pdf", "tester");
        var validated = await _store.RecordValidationAsync(item.Id, new(ReviewSignal.Red, ["Falscher Betrag"]), "engine");
        Assert.Equal(DocumentStatus.Rejected, validated!.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ReviewAsync(item.Id, true, " ", "reviewer"));
        var approved = await _store.ReviewAsync(item.Id, true, "PDF und SAP-Daten manuell geprüft.", "reviewer");
        Assert.Equal(DocumentStatus.Approved, approved!.Status);
        Assert.Equal(ReviewSignal.Red, approved.Signal);
        Assert.Null(await _store.ReviewAsync(item.Id, true, "zweiter Versuch", "reviewer"));
        Assert.Contains((await _store.EventsAsync(item.Id)).Select(x => x.Kind), x => x == "ManualReviewApproved");
    }

    [Fact]
    public async Task Creates_a_consistent_sqlite_backup()
    {
        var item = await _store.CreateAsync(new(DocumentDirection.Outgoing, 12, 22), "C".PadLeft(64, 'C'), "invoice.pdf", "tester");
        var backup = Path.Combine(_directory, "backup", "novanein.db");

        await _store.BackupDatabaseAsync(backup);

        Assert.True(File.Exists(backup));
        var restored = new DocumentStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DatabasePath"] = backup }).Build());
        Assert.Equal(item.Id, (await restored.GetAsync(item.Id))!.Id);
    }

    [Fact]
    public async Task Finds_existing_sap_identity_and_reports_referenced_pdf_hashes()
    {
        var hash = "D".PadLeft(64, 'D');
        var item = await _store.CreateAsync(new(DocumentDirection.Incoming, 13, 23), hash, "invoice.pdf", "tester");

        Assert.Equal(item.Id, (await _store.GetBySapAsync(DocumentDirection.Incoming, 13))!.Id);
        Assert.Null(await _store.GetBySapAsync(DocumentDirection.Outgoing, 13));
        Assert.Contains(hash, await _store.ListPdfHashesAsync());
    }

    [Fact]
    public async Task Reports_statistics_for_each_workflow_status()
    {
        await _store.CreateAsync(new(DocumentDirection.Incoming, 31, 41), "1".PadLeft(64, '1'), "received.pdf", "tester");
        var review = await _store.CreateAsync(new(DocumentDirection.Incoming, 32, 42), "2".PadLeft(64, '2'), "review.pdf", "tester");
        await _store.RecordValidationAsync(review.Id, new(ReviewSignal.Yellow, ["manuelle Prüfung"]), "engine");
        var attached = await _store.CreateAsync(new(DocumentDirection.Outgoing, 33, 43), "3".PadLeft(64, '3'), "attached.pdf", "tester");
        await _store.RecordValidationAsync(attached.Id, new(ReviewSignal.Green, []), "engine");
        await _store.MarkAttachedToSapAsync(attached.Id, "sap");
        var rejected = await _store.CreateAsync(new(DocumentDirection.Incoming, 34, 44), "4".PadLeft(64, '4'), "rejected.pdf", "tester");
        await _store.RecordValidationAsync(rejected.Id, new(ReviewSignal.Red, ["Abweichung"]), "engine");
        var approved = await _store.CreateAsync(new(DocumentDirection.Incoming, 35, 45), "5".PadLeft(64, '5'), "approved.pdf", "tester");
        await _store.RecordValidationAsync(approved.Id, new(ReviewSignal.Green, []), "engine");

        var statistics = await _store.StatisticsAsync();

        Assert.Equal(5, statistics.Total);
        Assert.Equal(1, statistics.Received);
        Assert.Equal(1, statistics.NeedsReview);
        Assert.Equal(1, statistics.Rejected);
        Assert.Equal(1, statistics.Approved);
        Assert.Equal(1, statistics.AttachedToSap);
        Assert.True(statistics.CreatedLast7Days >= 5);
        Assert.True(statistics.CreatedLast30Days >= 5);
    }

    [Fact]
    public async Task Reports_uploads_by_direction_for_a_requested_calendar_period()
    {
        await _store.CreateAsync(new(DocumentDirection.Incoming, 130, 230), "A".PadLeft(64, 'A'), "incoming.pdf", "tester");
        await _store.CreateAsync(new(DocumentDirection.Outgoing, 131, 231), "B".PadLeft(64, 'B'), "outgoing.pdf", "tester");
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        UploadPeriodStatistics current = await _store.UploadStatisticsAsync(today, today);
        UploadPeriodStatistics yesterday = await _store.UploadStatisticsAsync(today.AddDays(-1), today.AddDays(-1));

        Assert.Equal(2, current.Total);
        Assert.Equal(1, current.Incoming);
        Assert.Equal(1, current.Outgoing);
        Assert.Equal(0, yesterday.Total);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UploadStatisticsAsync(today, today.AddDays(-1)));
    }

    [Fact]
    public async Task Datev_package_preparation_moves_approved_document_to_packaged_and_audits_hash()
    {
        var item = await _store.CreateAsync(new(DocumentDirection.Incoming, 36, 46), "6".PadLeft(64, '6'), "package.pdf", "tester");
        await _store.RecordValidationAsync(item.Id, new(ReviewSignal.Green, []), "engine");

        var packaged = await _store.RecordDatevPackagePreparedAsync(item.Id, "/tmp/Eingangsrechnung-46.zip", "A".PadLeft(64, 'A'), DateTimeOffset.UtcNow, "datev-package-worker");

        Assert.Equal(DocumentStatus.Packaged, packaged!.Status);
        var audit = Assert.Single((await _store.EventsAsync(item.Id)).Where(x => x.Kind == "DatevPackagePrepared"));
        Assert.Contains("Eingangsrechnung-46.zip", audit.Detail);
        Assert.Contains("AAAAAAAA", audit.Detail);
    }

    [Fact]
    public async Task Sap_attachment_readback_does_not_regress_an_already_packaged_document()
    {
        var item = await _store.CreateAsync(new(DocumentDirection.Incoming, 37, 47), "7".PadLeft(64, '7'), "package-first.pdf", "tester");
        await _store.RecordValidationAsync(item.Id, new(ReviewSignal.Green, []), "engine");
        await _store.RecordDatevPackagePreparedAsync(item.Id, "/tmp/Eingangsrechnung-47.zip", "B".PadLeft(64, 'B'), DateTimeOffset.UtcNow, "datev-package-worker");

        var afterSap = await _store.MarkAttachedToSapAsync(item.Id, "sap-attachment-worker");

        Assert.Equal(DocumentStatus.Packaged, afterSap!.Status);
        Assert.Contains(await _store.EventsAsync(item.Id), x => x.Kind == "SapAttachmentVerified" && x.Detail.Contains("bereits erreichte", StringComparison.Ordinal));
    }

	[Fact]
	public async Task Credit_note_datev_release_is_approved_only_and_idempotently_audited()
	{
		var creditNote = await _store.CreateAsync(new(DocumentDirection.Incoming, 50, 80003, SapBusinessDocumentType.PurchaseCreditNote), "A".PadLeft(64, 'A'), "credit-note.pdf", "operator");
		Assert.False(await _store.RecordCreditNoteDatevReleaseAsync(creditNote.Id, "vor Prüfung", "reviewer"));
		await _store.RecordValidationAsync(creditNote.Id, new(ReviewSignal.Green, []), "engine");

		Assert.True(await _store.RecordCreditNoteDatevReleaseAsync(creditNote.Id, "Gutschrift und SAP-Daten geprüft.", "reviewer"));
		Assert.True(await _store.RecordCreditNoteDatevReleaseAsync(creditNote.Id, "zweiter Aufruf", "reviewer"));
		Assert.True(await _store.HasCreditNoteDatevReleaseAsync(creditNote.Id));
		Assert.Single((await _store.EventsAsync(creditNote.Id)).Where(x => x.Kind == "CreditNoteDatevReleaseApproved"));

		var invoice = await _store.CreateAsync(new(DocumentDirection.Incoming, 51, 900500, SapBusinessDocumentType.PurchaseInvoice), "B".PadLeft(64, 'B'), "invoice.pdf", "operator");
		await _store.RecordValidationAsync(invoice.Id, new(ReviewSignal.Green, []), "engine");
		Assert.False(await _store.RecordCreditNoteDatevReleaseAsync(invoice.Id, "falscher Belegtyp", "reviewer"));
	}

    [Fact]
    public async Task Lists_recent_document_activity_newest_first_with_document_context()
    {
        await _store.CreateAsync(new(DocumentDirection.Outgoing, 38, 48, SapBusinessDocumentType.Invoice), "8".PadLeft(64, '8'), "outgoing.pdf", "operator");
        var incoming = await _store.CreateAsync(new(DocumentDirection.Incoming, 39, 49, SapBusinessDocumentType.PurchaseInvoice), "9".PadLeft(64, '9'), "incoming.pdf", "operator");
        await _store.RecordValidationAsync(incoming.Id, new(ReviewSignal.Yellow, ["OCR unsicher"]), "validation-worker");

        var activity = await _store.RecentActivityAsync(2);

        Assert.Equal(2, activity.Count);
        Assert.Equal("ValidationCompleted", activity[0].Kind);
        Assert.Equal(incoming.Id, activity[0].DocumentId);
        Assert.Equal(49, activity[0].DocNum);
        Assert.Equal(DocumentDirection.Incoming, activity[0].Direction);
        Assert.Equal(DocumentStatus.NeedsReview, activity[0].CurrentStatus);
        Assert.Equal("DocumentReceived", activity[1].Kind);
    }
}
