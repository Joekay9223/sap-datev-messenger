using Microsoft.Extensions.Configuration;
using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class DocumentJobQueueTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-jobs-{Guid.NewGuid():N}");
    private string _databasePath = null!;
    private DocumentJobQueue _queue = null!;
    private DocumentStore _documents = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(_directory, "archive.db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DatabasePath"] = _databasePath }).Build();
        _queue = new DocumentJobQueue(configuration);
        _documents = new DocumentStore(configuration);
        await _documents.InitializeAsync();
        await _queue.InitializeAsync();
    }
    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }

    [Fact]
    public async Task Claims_once_and_recovers_interrupted_job()
    {
        var queued = await _queue.EnqueueAsync(Guid.NewGuid(), DocumentJobKind.ValidateIncoming);
        var claimed = await _queue.ClaimNextAsync();
        Assert.NotNull(claimed);
        Assert.Equal(queued.Id, claimed!.Id);
        Assert.Equal(1, claimed.Attempt);
        Assert.Null(await _queue.ClaimNextAsync());
        Assert.Equal(1, await _queue.RecoverInterruptedAsync());
        var resumed = await _queue.ClaimNextAsync();
        Assert.Equal(queued.Id, resumed!.Id);
        Assert.Equal(2, resumed.Attempt);
        await _queue.CompleteAsync(resumed.Id);
        Assert.Null(await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task Prevents_duplicate_job_kind_for_document()
    {
        var documentId = Guid.NewGuid();
        await _queue.EnqueueAsync(documentId, DocumentJobKind.CreateDatevPackage);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _queue.EnqueueAsync(documentId, DocumentJobKind.CreateDatevPackage));
    }

    [Fact]
    public async Task Requeues_a_rejected_document_for_an_audited_validation_retry()
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 91, 901), new string('A', 64), "invoice.pdf", "tester");
        var job = await _queue.EnqueueAsync(document.Id, DocumentJobKind.ValidateIncoming);
        Assert.Equal(job.Id, (await _queue.ClaimNextAsync(DocumentJobKind.ValidateIncoming))!.Id);
        await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Red, ["Parserfehler"]), "validation-worker");
        await _queue.CompleteAsync(job.Id);

        Assert.True(await _queue.RetryValidationAsync(document.Id, "Parser wurde für das reale Layout korrigiert.", "release-operator"));

        var reset = await _documents.GetAsync(document.Id);
        Assert.Equal(DocumentStatus.Received, reset!.Status);
        Assert.Null(reset.Signal);
        var retry = await _queue.ClaimNextAsync(DocumentJobKind.ValidateIncoming);
        Assert.NotNull(retry);
        Assert.Equal(1, retry!.Attempt);
        Assert.Contains(await _documents.EventsAsync(document.Id), item => item.Kind == "ValidationRetryRequested" && item.Actor == "release-operator");
    }

    [Fact]
    public async Task Requeues_a_yellow_document_after_openai_becomes_available()
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 92, 902), new string('B', 64), "scan.pdf", "tester");
        var job = await _queue.EnqueueAsync(document.Id, DocumentJobKind.ValidateIncoming);
        Assert.Equal(job.Id, (await _queue.ClaimNextAsync(DocumentJobKind.ValidateIncoming))!.Id);
        await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Yellow, ["OpenAI-Dokumentinterpretation nicht verfügbar."]), "validation-worker");
        await _queue.CompleteAsync(job.Id);

        Assert.True(await _queue.RetryValidationAsync(document.Id, "OpenAI-Dokumentinterpretation ist jetzt verfügbar.", "release-operator"));

        Assert.Equal(DocumentStatus.Received, (await _documents.GetAsync(document.Id))!.Status);
        Assert.NotNull(await _queue.ClaimNextAsync(DocumentJobKind.ValidateIncoming));
    }

    [Fact]
    public async Task Replaces_the_pdf_after_a_red_validation_and_requeues_the_validation_atomically()
    {
        var originalHash = new string('1', 64);
        var replacementHash = new string('2', 64);
        var document = await _documents.CreateAsync(new(DocumentDirection.Outgoing, 93, 903), originalHash, "falsche-rechnung.pdf", "tester");
        var job = await _queue.EnqueueAsync(document.Id, DocumentJobKind.ValidateOutgoing);
        Assert.Equal(job.Id, (await _queue.ClaimNextAsync(DocumentJobKind.ValidateOutgoing))!.Id);
        await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Red, ["Rechnungsnummer stimmt nicht überein."]), "validation-worker");
        await _queue.CompleteAsync(job.Id);

        var replaced = await _queue.ReplacePdfAndRetryValidationAsync(
            _documents,
            document.Id,
            replacementHash,
            "richtige-rechnung.pdf",
            "web-cockpit");

        Assert.NotNull(replaced);
        Assert.Equal(DocumentStatus.Received, replaced!.Status);
        Assert.Null(replaced.Signal);
        Assert.Equal(replacementHash, replaced.PdfSha256);
        Assert.Equal("richtige-rechnung.pdf", replaced.OriginalFileName);
        var retry = Assert.IsType<DocumentJob>(await _queue.GetAsync(document.Id, DocumentJobKind.ValidateOutgoing));
        Assert.Equal(DocumentJobState.Queued, retry.State);
        Assert.Equal(0, retry.Attempt);
        var audit = Assert.Single((await _documents.EventsAsync(document.Id)).Where(item => item.Kind == "PdfReplaced"));
        Assert.Equal("web-cockpit", audit.Actor);
        Assert.Contains(originalHash, audit.Detail);
        Assert.Contains(replacementHash, audit.Detail);
    }

    [Fact]
    public async Task Pdf_replacement_after_a_package_failure_resets_validation_and_removes_stale_package_job()
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 94, 904), new string('3', 64), "rechnung.pdf", "tester");
        var validation = await _queue.EnqueueAsync(document.Id, DocumentJobKind.ValidateIncoming);
        Assert.Equal(validation.Id, (await _queue.ClaimNextAsync(DocumentJobKind.ValidateIncoming))!.Id);
        await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Green, []), "validation-worker");
        await _queue.CompleteAsync(validation.Id);
        var package = await _queue.EnqueueAsync(document.Id, DocumentJobKind.CreateDatevPackage);
        Assert.Equal(package.Id, (await _queue.ClaimNextAsync(DocumentJobKind.CreateDatevPackage))!.Id);
        await SetAttemptAsync(package.Id, 5);
        Assert.Equal(DocumentJobState.Failed, await _queue.FailAsync(package.Id, "DATEV-Paket fehlgeschlagen.", _documents, "datev-worker"));

        var replaced = await _queue.ReplacePdfAndRetryValidationAsync(
            _documents,
            document.Id,
            new string('4', 64),
            "andere-rechnung.pdf",
            "web-cockpit");

        Assert.NotNull(replaced);
        Assert.Equal(DocumentStatus.Received, replaced!.Status);
        Assert.Equal(new string('4', 64), replaced.PdfSha256);
        Assert.Null(await _queue.GetAsync(document.Id, DocumentJobKind.CreateDatevPackage));
        var validationRetry = Assert.IsType<DocumentJob>(await _queue.GetAsync(document.Id, DocumentJobKind.ValidateIncoming));
        Assert.Equal(DocumentJobState.Queued, validationRetry.State);
        Assert.Contains(await _documents.EventsAsync(document.Id), item => item.Kind == "PdfReplaced");
    }

    [Fact]
    public async Task Retry_keeps_document_received_and_writes_no_failure_event()
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 101, 201), "D".PadLeft(64, 'D'), "invoice.pdf", "tester");
        await _queue.EnqueueAsync(document.Id, DocumentJobKind.ValidateIncoming);
        var claimed = Assert.IsType<DocumentJob>(await _queue.ClaimNextAsync());

        var state = await _queue.FailAsync(claimed.Id, "SAP vorübergehend nicht erreichbar.", _documents, "test-worker");

        Assert.Equal(DocumentJobState.Queued, state);
        Assert.Equal(DocumentStatus.Received, (await _documents.GetAsync(document.Id))!.Status);
        Assert.DoesNotContain(await _documents.EventsAsync(document.Id), item => item.Kind == "DocumentJobFailed");
    }

    [Fact]
    public async Task Final_failure_atomically_marks_document_and_writes_safe_audit_event()
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 102, 202), "E".PadLeft(64, 'E'), "invoice.pdf", "tester");
        var queued = await _queue.EnqueueAsync(document.Id, DocumentJobKind.ValidateIncoming);

        for (var attempt = 1; attempt < 5; attempt++)
        {
            var retried = Assert.IsType<DocumentJob>(await _queue.ClaimNextAsync());
            Assert.Equal(attempt, retried.Attempt);
            Assert.Equal(DocumentJobState.Queued, await _queue.FailAsync(retried.Id, "Temporärer Fehler.", _documents, "test-worker"));
            Assert.Equal(DocumentStatus.Received, (await _documents.GetAsync(document.Id))!.Status);
            Assert.DoesNotContain(await _documents.EventsAsync(document.Id), item => item.Kind == "DocumentJobFailed");
            await MakeDueAsync(queued.Id);
        }

        var final = Assert.IsType<DocumentJob>(await _queue.ClaimNextAsync());
        var unsafeError = "Upload fehlgeschlagen. api_key=super-secret-value C:\\Users\\Example Maintainer\\secret.pdf https://user:password@example.test?q=token\r\n" + new string('X', 500);
        var state = await _queue.FailAsync(final.Id, unsafeError, _documents, "test-worker");

        Assert.Equal(5, final.Attempt);
        Assert.Equal(DocumentJobState.Failed, state);
        Assert.Equal(DocumentStatus.Failed, (await _documents.GetAsync(document.Id))!.Status);
        var failure = Assert.Single((await _documents.EventsAsync(document.Id)).Where(item => item.Kind == "DocumentJobFailed"));
        Assert.Equal("test-worker", failure.Actor);
        Assert.StartsWith("ValidateIncoming endgültig fehlgeschlagen:", failure.Detail);
        Assert.True(failure.Detail.Length <= 320);
        Assert.DoesNotContain("super-secret-value", failure.Detail);
        Assert.DoesNotContain("John", failure.Detail);
        Assert.DoesNotContain("Kaiser", failure.Detail);
        Assert.DoesNotContain("password@example", failure.Detail);
        Assert.DoesNotContain('\r', failure.Detail);
        Assert.DoesNotContain('\n', failure.Detail);
    }

    [Fact]
    public async Task Final_failure_does_not_overwrite_an_already_persisted_validation_result()
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 103, 203), "F".PadLeft(64, 'F'), "invoice.pdf", "tester");
        var queued = await _queue.EnqueueAsync(document.Id, DocumentJobKind.ValidateIncoming);
        var claimed = Assert.IsType<DocumentJob>(await _queue.ClaimNextAsync());
        await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Green, []), "validation-worker");
        await SetAttemptAsync(queued.Id, 5);

        var state = await _queue.FailAsync(claimed.Id, "Abbruch nach gespeicherter Validierung.", _documents, "test-worker");

        Assert.Equal(DocumentJobState.Completed, state);
        Assert.Equal(DocumentStatus.Approved, (await _documents.GetAsync(document.Id))!.Status);
        Assert.DoesNotContain(await _documents.EventsAsync(document.Id), item => item.Kind == "DocumentJobFailed");
    }

    [Fact]
    public async Task Validation_retry_refuses_a_failure_that_belongs_to_the_sap_attachment_job()
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 104, 204), "G".PadLeft(64, 'G'), "invoice.pdf", "tester");
        var validation = await _queue.EnqueueAsync(document.Id, DocumentJobKind.ValidateIncoming);
        Assert.Equal(validation.Id, (await _queue.ClaimNextAsync(DocumentJobKind.ValidateIncoming))!.Id);
        await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Green, []), "validation-worker");
        await _queue.CompleteAsync(validation.Id);
        var attachment = await _queue.EnqueueAsync(document.Id, DocumentJobKind.AttachToSap);
        Assert.Equal(attachment.Id, (await _queue.ClaimNextAsync(DocumentJobKind.AttachToSap))!.Id);
        await SetAttemptAsync(attachment.Id, 5);
        Assert.Equal(DocumentJobState.Failed, await _queue.FailAsync(attachment.Id, "SAP-Anhang fehlgeschlagen.", _documents, "attachment-worker"));

        Assert.False(await _queue.RetryValidationAsync(document.Id, "Falscher Retry-Typ.", "tester"));
        Assert.Equal(DocumentStatus.Failed, (await _documents.GetAsync(document.Id))!.Status);
    }

    [Fact]
    public async Task Datev_package_retry_restores_approved_status_and_requeues_only_terminal_failure()
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 105, 205), "H".PadLeft(64, 'H'), "invoice.pdf", "tester");
        await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Green, []), "validation-worker");
        var job = await _queue.EnqueueAsync(document.Id, DocumentJobKind.CreateDatevPackage);
        Assert.Equal(job.Id, (await _queue.ClaimNextAsync(DocumentJobKind.CreateDatevPackage))!.Id);
        await SetAttemptAsync(job.Id, 5);
        Assert.Equal(DocumentJobState.Failed, await _queue.FailAsync(job.Id, "DATEV-Paketbau fehlgeschlagen.", _documents, "datev-package-worker"));
        Assert.Equal(DocumentStatus.Failed, (await _documents.GetAsync(document.Id))!.Status);

        Assert.True(await _queue.RetryDatevPackageAsync(document.Id, "web-cockpit"));
        Assert.Equal(DocumentStatus.Approved, (await _documents.GetAsync(document.Id))!.Status);
        var retry = await _queue.GetAsync(document.Id, DocumentJobKind.CreateDatevPackage);
        Assert.Equal(DocumentJobState.Queued, retry!.State);
        Assert.Equal(0, retry.Attempt);
        Assert.Contains(await _documents.EventsAsync(document.Id), item => item.Kind == "DatevPackageRetryRequested");
        Assert.False(await _queue.RetryDatevPackageAsync(document.Id, "web-cockpit"));
    }

    [Fact]
    public async Task Datev_package_retry_requeues_completed_job_without_package_evidence()
    {
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 106, 206), "I".PadLeft(64, 'I'), "invoice.pdf", "tester");
        await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Green, []), "validation-worker");
        var job = await _queue.EnqueueAsync(document.Id, DocumentJobKind.CreateDatevPackage);
        Assert.Equal(job.Id, (await _queue.ClaimNextAsync(DocumentJobKind.CreateDatevPackage))!.Id);
        await _queue.CompleteAsync(job.Id);

        Assert.True(await _queue.RetryDatevPackageAsync(document.Id, "web-cockpit"));
        Assert.Equal(DocumentStatus.Approved, (await _documents.GetAsync(document.Id))!.Status);
        var retry = await _queue.GetAsync(document.Id, DocumentJobKind.CreateDatevPackage);
        Assert.Equal(DocumentJobState.Queued, retry!.State);
        Assert.Equal(0, retry.Attempt);
        Assert.Contains(await _documents.EventsAsync(document.Id), item => item.Kind == "DatevPackageRebuildRequested");
        Assert.False(await _queue.RetryDatevPackageAsync(document.Id, "web-cockpit"));
    }

	[Fact]
	public async Task Datev_package_retry_restarts_a_queued_backoff_after_the_technical_cause_was_fixed()
	{
		var document = await _documents.CreateAsync(new(DocumentDirection.Outgoing, 107, 207), new string('J', 64), "invoice.pdf", "tester");
		await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Green, []), "validation-worker");
		await _queue.EnqueueAsync(document.Id, DocumentJobKind.CreateDatevPackage);
		var claimed = Assert.IsType<DocumentJob>(await _queue.ClaimNextAsync(DocumentJobKind.CreateDatevPackage));
		Assert.Equal(DocumentJobState.Queued, await _queue.FailAsync(claimed.Id, "XSD-Fehler wurde inzwischen behoben.", _documents, "datev-package-worker"));

		Assert.True(await _queue.RetryDatevPackageAsync(document.Id, "release-operator"));
		var restarted = Assert.IsType<DocumentJob>(await _queue.GetAsync(document.Id, DocumentJobKind.CreateDatevPackage));
		Assert.Equal(DocumentJobState.Queued, restarted.State);
		Assert.Equal(0, restarted.Attempt);
		Assert.Null(restarted.LastError);
		Assert.True(restarted.NotBefore <= DateTimeOffset.UtcNow.AddSeconds(1));
		Assert.Contains(await _documents.EventsAsync(document.Id), item => item.Kind == "DatevPackageRetryRequested");
	}

    private async Task MakeDueAsync(Guid jobId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE document_jobs SET not_before=$notBefore WHERE id=$id";
        command.Parameters.AddWithValue("$notBefore", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
        command.Parameters.AddWithValue("$id", jobId.ToString());
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task SetAttemptAsync(Guid jobId, int attempt)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE document_jobs SET attempt=$attempt WHERE id=$id";
        command.Parameters.AddWithValue("$attempt", attempt);
        command.Parameters.AddWithValue("$id", jobId.ToString());
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
