using Microsoft.Extensions.Configuration;
using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class BttnextEvidenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-bttnext-{Guid.NewGuid():N}");
    private TransferEvidenceStore _store = null!;
    private DatevTransferRequestStore _requests = null!;
    private DocumentStore _documents = null!;

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DatabasePath"] = Path.Combine(_directory, "archive.db")
        }).Build();
        _store = new TransferEvidenceStore(configuration);
        _documents = new DocumentStore(configuration);
        _requests = new DatevTransferRequestStore(configuration, _store);
        await _documents.InitializeAsync();
        await _store.InitializeAsync();
        await _requests.InitializeAsync();
    }

    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }

    [Fact]
    public async Task Requires_both_events_for_same_delivered_package()
    {
        var id = await RegisterDeliveredPackageAsync(42, "Eingangsrechnung-42.zip", new string('a', 64), DateTimeOffset.Parse("2026-07-10T09:59:00Z"));
        var upload = BttnextLogParser.Parse("2026-07-10 UploadSucceeded PackageFileName=Eingangsrechnung-42.zip", DateTimeOffset.Parse("2026-07-10T10:00:00Z"));
        Assert.NotNull(upload);
        Assert.True(await _store.RecordAsync(upload!));
        Assert.False((await _store.GetAsync(id))!.IsTransferred);
        var finalized = BttnextLogParser.Parse("2026-07-10 JobFinalized Package=Eingangsrechnung-42.zip", DateTimeOffset.Parse("2026-07-10T10:01:00Z"));
        Assert.True(await _store.RecordAsync(finalized!));
        Assert.True((await _store.GetAsync(id))!.IsTransferred);
    }

    [Fact]
    public async Task Ignores_unrelated_and_uncorrelated_log_lines()
    {
        Assert.Null(BttnextLogParser.Parse("UploadSucceeded without a package", DateTimeOffset.UtcNow));
        var unregistered = BttnextLogParser.Parse("JobFinalized File=other.zip", DateTimeOffset.UtcNow);
        Assert.False(await _store.RecordAsync(unregistered!));
    }

    [Fact]
    public void Parses_observed_bttnext_state_change_format()
    {
        var line = "25.06.26 07:14:00.578 INF DocumentInProgress.set_DocumentState fileName=INVOICE-INV128771.zip oldDocumentState=JobCreated newDocumentState=UploadSucceeded";
        var result = BttnextLogParser.Parse(line, DateTimeOffset.UtcNow);
        Assert.NotNull(result);
        Assert.Equal(BttnextEventType.UploadSucceeded, result!.Type);
        Assert.Equal("INVOICE-INV128771.zip", result.PackageFileName);
    }

    [Fact]
    public void Parses_actual_bttnext_final_log_states()
    {
        var moved = BttnextLogParser.Parse("09.07.26 08:33:11.249 INF category=invoices_received file=INVOICE-728129890745.zip state=JobDeletedOrMoved", DateTimeOffset.UtcNow);
        var fileMoved = BttnextLogParser.Parse("09.07.26 08:33:12.249 INF category=invoices_received file=INVOICE-728129890745.zip state=FileDeletedOrMoved", DateTimeOffset.UtcNow);
        var finalized = BttnextLogParser.Parse("09.07.26 08:33:13.455 INF category=invoices_received file=INVOICE-728129890745.zip state=JobProtocolEntriesSuccess", DateTimeOffset.UtcNow);
        Assert.Equal(BttnextEventType.UploadSucceeded, moved!.Type);
        Assert.Equal(BttnextEventType.UploadSucceeded, fileMoved!.Type);
        Assert.Equal(BttnextEventType.JobFinalized, finalized!.Type);
        Assert.Equal(moved.PackageFileName, finalized.PackageFileName);
    }

    [Fact]
    public void Parses_observed_state_log_format_and_timestamp()
    {
        var line = "08.07.26 10:09:59.450 INF mandant=000000-00000 category=invoices_received documentName=Rechnungseingang file=INVOICE-TATSZBLX-0003.zip state=JobFinalized";
        var utc = TimeZoneInfo.CreateCustomTimeZone("UTC", TimeSpan.Zero, "UTC", "UTC");
        var result = BttnextLogParser.Parse(line, DateTimeOffset.UtcNow, utc);
        Assert.NotNull(result);
        Assert.Equal(BttnextEventType.JobFinalized, result!.Type);
        Assert.Equal("INVOICE-TATSZBLX-0003.zip", result.PackageFileName);
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T10:09:59.450Z"), result.OccurredAt);
    }

    [Fact]
    public async Task Reconciles_bttnext_renamed_archive_by_zip_hash()
    {
        var archive = Path.Combine(_directory, "archive"); Directory.CreateDirectory(archive);
        var path = Path.Combine(archive, "INVOICE-RENAMED-42.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("document.xml").Open())) await writer.WriteAsync("<archive />");
        await using var input = File.OpenRead(path); using var sha = System.Security.Cryptography.SHA256.Create(); var hash = Convert.ToHexString(await sha.ComputeHashAsync(input));
        var id = await RegisterDeliveredPackageAsync(142, "Eingangsrechnung-42.zip", hash, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(1, await _store.ReconcileArchiveAsync(archive));
        var eventData = BttnextLogParser.Parse("JobFinalized file=INVOICE-RENAMED-42.zip", DateTimeOffset.UtcNow);
        Assert.True(await _store.RecordAsync(eventData!));
        Assert.NotNull((await _store.GetAsync(id))!.JobFinalizedAt);
    }

    [Fact]
    public async Task Replays_events_seen_before_bridge_receipt()
    {
        var document = await RegisterPackageAsync(242, "Eingangsrechnung-242.zip", new string('b', 64));
        var request = await _requests.RequestAsync(document.Id, new string('b', 64), "tester");
        var claimed = await _requests.ClaimNextAsync();
        var eventTime = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.False(await _store.RecordAsync(new(BttnextEventType.UploadSucceeded, "Eingangsrechnung-242.zip", eventTime, "UploadSucceeded File=Eingangsrechnung-242.zip")));
        Assert.False(await _store.RecordAsync(new(BttnextEventType.JobFinalized, "Eingangsrechnung-242.zip", eventTime.AddSeconds(2), "JobFinalized File=Eingangsrechnung-242.zip")));
        await _requests.MarkBridgeStagedAsync(claimed!.Id, DateTimeOffset.UtcNow);
        await _requests.MarkWatchfolderDeliveredAsync(claimed.Id, eventTime.AddSeconds(-1));

        Assert.True(await _store.ReconcileDocumentAsync(document.Id));
        Assert.True((await _store.GetAsync(document.Id))!.IsTransferred);
        Assert.Equal("finalized", (await _requests.GetByDocumentAsync(document.Id))!.Status);
        Assert.Equal(DocumentStatus.Transferred, (await _documents.GetAsync(document.Id))!.Status);
    }

    [Fact]
    public async Task First_cursor_initialization_starts_at_current_file_end()
    {
        var logs = Path.Combine(_directory, "logs"); Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "btt.log"); await File.WriteAllTextAsync(path, "historisch\n");
        await _store.InitializeLogCursorsAsync(logs);
        Assert.Equal(new FileInfo(path).Length, await _store.GetLogCursorAsync(path));
        await File.AppendAllTextAsync(path, "neu\n");
        await _store.InitializeLogCursorsAsync(logs);
        Assert.NotEqual(new FileInfo(path).Length, await _store.GetLogCursorAsync(path));
    }

    [Fact]
    public async Task Duplicate_transfer_request_is_idempotent_and_does_not_requeue_active_work()
    {
        var document = await RegisterPackageAsync(342, "Eingangsrechnung-342.zip", new string('c', 64));
        var first = await _requests.RequestAsync(document.Id, new string('c', 64), "tester");
        var claimed = await _requests.ClaimNextAsync();
        Assert.Equal("transferring", claimed!.Status);
        var duplicate = await _requests.RequestAsync(document.Id, new string('c', 64), "tester-2");
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal("transferring", duplicate.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _requests.RequestAsync(document.Id, new string('d', 64), "tester"));
    }

    [Fact]
    public async Task Startup_recovery_queues_only_new_green_invoice_packages_once()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        var eligible = await RegisterPackageAsync(542, "Eingangsrechnung-542.zip", new string('1', 64), cutoff.AddMinutes(1));
        var old = await RegisterPackageAsync(543, "Eingangsrechnung-543.zip", new string('2', 64), cutoff.AddMinutes(-1));
        var manual = await RegisterPackageAsync(544, "Eingangsrechnung-544.zip", new string('3', 64), cutoff.AddMinutes(2), ReviewSignal.Yellow);

        Assert.Equal(1, await _requests.EnsureGreenPackagesQueuedAsync(cutoff, "green-only-startup"));
        Assert.NotNull(await _requests.GetByDocumentAsync(eligible.Id));
        Assert.Null(await _requests.GetByDocumentAsync(old.Id));
        Assert.Null(await _requests.GetByDocumentAsync(manual.Id));
        Assert.Equal(0, await _requests.EnsureGreenPackagesQueuedAsync(cutoff, "green-only-startup"));
    }

    [Fact]
    public async Task Startup_recovery_queues_manually_approved_invoice_packages_once()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        var yellow = await RegisterPackageAsync(642, "Eingangsrechnung-642.zip", new string('4', 64), cutoff.AddMinutes(1), ReviewSignal.Yellow);
        var red = await RegisterPackageAsync(643, "Eingangsrechnung-643.zip", new string('5', 64), cutoff.AddMinutes(2), ReviewSignal.Red);
        var creditNote = await RegisterPackageAsync(644, "Eingangsgutschrift-644.zip", new string('6', 64), cutoff.AddMinutes(3), ReviewSignal.Green, SapBusinessDocumentType.PurchaseCreditNote);

        Assert.Equal(2, await _requests.EnsureApprovedInvoicePackagesQueuedAsync(cutoff, "approved-invoice-startup"));
        Assert.NotNull(await _requests.GetByDocumentAsync(yellow.Id));
        Assert.NotNull(await _requests.GetByDocumentAsync(red.Id));
        Assert.Null(await _requests.GetByDocumentAsync(creditNote.Id));
        Assert.Equal(0, await _requests.EnsureApprovedInvoicePackagesQueuedAsync(cutoff, "approved-invoice-startup"));
    }

    [Fact]
    public async Task Ignores_same_named_events_older_than_current_delivery()
    {
        var deliveredAt = DateTimeOffset.UtcNow;
        var id = await RegisterDeliveredPackageAsync(442, "Eingangsrechnung-442.zip", new string('e', 64), deliveredAt);
        Assert.False(await _store.RecordAsync(new(BttnextEventType.UploadSucceeded, "Eingangsrechnung-442.zip", deliveredAt.AddSeconds(-1), "old upload")));
        Assert.Null((await _store.GetAsync(id))!.UploadSucceededAt);
    }

    private async Task<Guid> RegisterDeliveredPackageAsync(int docNum, string fileName, string hash, DateTimeOffset deliveredAt)
    {
        var document = await RegisterPackageAsync(docNum, fileName, hash);
        var request = await _requests.RequestAsync(document.Id, hash, "tester");
        var claimed = await _requests.ClaimNextAsync();
        Assert.Equal(request.Id, claimed!.Id);
        await _requests.MarkBridgeStagedAsync(request.Id, deliveredAt.AddSeconds(-1));
        await _requests.MarkWatchfolderDeliveredAsync(request.Id, deliveredAt);
        return document.Id;
    }

    private async Task<DocumentRecord> RegisterPackageAsync(int docNum, string fileName, string hash, DateTimeOffset? preparedAt = null, ReviewSignal signal = ReviewSignal.Green, SapBusinessDocumentType type = SapBusinessDocumentType.PurchaseInvoice)
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, docNum, docNum, type), docNum.ToString("X64"), "invoice.pdf", "tester");
        document = (await _documents.RecordValidationAsync(document.Id, new(signal, signal == ReviewSignal.Green ? [] : ["manuelle Prüfung"]), "validator"))!;
        if (signal != ReviewSignal.Green)
            document = (await _documents.ReviewAsync(document.Id, true, "Beleg fachlich manuell geprüft.", "reviewer"))!;
        var packagePreparedAt = preparedAt ?? DateTimeOffset.UtcNow.AddMinutes(-2);
        await _store.RegisterPackageAsync(document.Id, hash, fileName, packagePreparedAt);
        document = (await _documents.RecordDatevPackagePreparedAsync(document.Id, fileName, hash, packagePreparedAt, "test", default))!;
        return document;
    }
}
