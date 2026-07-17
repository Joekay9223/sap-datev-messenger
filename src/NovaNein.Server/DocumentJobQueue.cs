using Microsoft.Data.Sqlite;

using System.Globalization;
using System.Text.RegularExpressions;
using NovaNein.Domain;

namespace NovaNein.Server;

public enum DocumentJobKind { ValidateIncoming, GenerateOutgoing, CreateDatevPackage, MonitorTransfer, ValidateOutgoing, AttachToSap }
public enum DocumentJobState { Queued, Running, Completed, Failed }
public sealed record DocumentJob(Guid Id, Guid DocumentId, DocumentJobKind Kind, DocumentJobState State, int Attempt, DateTimeOffset NotBefore, string? LastError);

public sealed class DocumentJobQueue(IConfiguration configuration)
{
    private const int MaximumAttempts = 5;
    private readonly string _connectionString = $"Data Source={configuration["Storage:DatabasePath"] ?? "data/novanein.db"}";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(_connectionString).DataSource);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = """
            CREATE TABLE IF NOT EXISTS document_jobs (
              id TEXT PRIMARY KEY, document_id TEXT NOT NULL, job_kind INTEGER NOT NULL, state INTEGER NOT NULL,
              attempt INTEGER NOT NULL, not_before TEXT NOT NULL, locked_at TEXT NULL, last_error TEXT NULL,
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
              UNIQUE(document_id, job_kind)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DocumentJob> EnqueueAsync(Guid documentId, DocumentJobKind kind, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new DocumentJob(Guid.NewGuid(), documentId, kind, DocumentJobState.Queued, 0, now, null);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "INSERT INTO document_jobs(id,document_id,job_kind,state,attempt,not_before,created_at,updated_at) VALUES($id,$document,$kind,$state,0,$notBefore,$created,$updated)";
        command.Parameters.AddWithValue("$id", job.Id.ToString()); command.Parameters.AddWithValue("$document", documentId.ToString()); command.Parameters.AddWithValue("$kind", (int)kind); command.Parameters.AddWithValue("$state", (int)DocumentJobState.Queued); command.Parameters.AddWithValue("$notBefore", now.ToString("O")); command.Parameters.AddWithValue("$created", now.ToString("O")); command.Parameters.AddWithValue("$updated", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return job;
    }

    public async Task<DocumentJob> EnsureEnqueuedAsync(Guid documentId, DocumentJobKind kind, CancellationToken cancellationToken = default)
    {
        try { return await EnqueueAsync(documentId, kind, cancellationToken); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand(); command.CommandText = "SELECT id,document_id,job_kind,state,attempt,not_before,last_error FROM document_jobs WHERE document_id=$document AND job_kind=$kind";
            command.Parameters.AddWithValue("$document", documentId.ToString()); command.Parameters.AddWithValue("$kind", (int)kind);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) return new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), (DocumentJobKind)reader.GetInt32(2), (DocumentJobState)reader.GetInt32(3), reader.GetInt32(4), ParseTimestamp(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6));
            throw;
        }
    }

    public async Task<DocumentJob?> GetAsync(Guid documentId, DocumentJobKind kind, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,document_id,job_kind,state,attempt,not_before,last_error FROM document_jobs WHERE document_id=$document AND job_kind=$kind";
        command.Parameters.AddWithValue("$document", documentId.ToString()); command.Parameters.AddWithValue("$kind", (int)kind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), (DocumentJobKind)reader.GetInt32(2), (DocumentJobState)reader.GetInt32(3), reader.GetInt32(4), ParseTimestamp(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6))
            : null;
    }

    /// <summary>
    /// Requeues a terminal DATEV package job when either the job failed or it was marked completed
    /// before a package evidence record was persisted. The caller must check for existing package
    /// evidence first so a valid package can never be rebuilt accidentally.
    /// </summary>
    public async Task<bool> RetryDatevPackageAsync(Guid documentId, string actor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Ein Akteur ist erforderlich.", nameof(actor));
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var lookup = connection.CreateCommand(); lookup.Transaction = transaction;
        lookup.CommandText = "SELECT status FROM documents WHERE id=$id"; lookup.Parameters.AddWithValue("$id", documentId.ToString());
        var documentStatusValue = await lookup.ExecuteScalarAsync(cancellationToken);
        if (documentStatusValue is null)
        { await transaction.RollbackAsync(cancellationToken); return false; }
        var documentStatus = (DocumentStatus)Convert.ToInt32(documentStatusValue);

        var job = connection.CreateCommand(); job.Transaction = transaction;
		job.CommandText = "SELECT state,last_error FROM document_jobs WHERE document_id=$document AND job_kind=$kind";
        job.Parameters.AddWithValue("$document", documentId.ToString()); job.Parameters.AddWithValue("$kind", (int)DocumentJobKind.CreateDatevPackage);
		await using var jobReader = await job.ExecuteReaderAsync(cancellationToken);
		if (!await jobReader.ReadAsync(cancellationToken))
		{ await transaction.RollbackAsync(cancellationToken); return false; }
		var jobState = (DocumentJobState)jobReader.GetInt32(0);
		var lastError = jobReader.IsDBNull(1) ? null : jobReader.GetString(1);
		await jobReader.DisposeAsync();

        var retryFailedJob = documentStatus == DocumentStatus.Failed && jobState == DocumentJobState.Failed;
        var rebuildMissingEvidence = jobState == DocumentJobState.Completed &&
            documentStatus is DocumentStatus.Approved or DocumentStatus.AttachedToSap;
		var restartBackoffAfterFix = jobState == DocumentJobState.Queued && !string.IsNullOrWhiteSpace(lastError)
			&& documentStatus is DocumentStatus.Approved or DocumentStatus.AttachedToSap;
		if (!retryFailedJob && !rebuildMissingEvidence && !restartBackoffAfterFix)
        { await transaction.RollbackAsync(cancellationToken); return false; }

        var attachment = connection.CreateCommand(); attachment.Transaction = transaction;
        attachment.CommandText = "SELECT COUNT(*) FROM audit_events WHERE document_id=$id AND kind='SapAttachmentVerified'";
        attachment.Parameters.AddWithValue("$id", documentId.ToString());
        var restoredStatus = retryFailedJob
            ? Convert.ToInt32(await attachment.ExecuteScalarAsync(cancellationToken)) > 0
                ? DocumentStatus.AttachedToSap
                : DocumentStatus.Approved
            : documentStatus;
        var now = DateTimeOffset.UtcNow.ToString("O");

        if (retryFailedJob)
        {
            var restore = connection.CreateCommand(); restore.Transaction = transaction;
            restore.CommandText = "UPDATE documents SET status=$status,updated_at=$updated WHERE id=$id AND status=$failed";
            restore.Parameters.AddWithValue("$status", (int)restoredStatus); restore.Parameters.AddWithValue("$updated", now); restore.Parameters.AddWithValue("$id", documentId.ToString()); restore.Parameters.AddWithValue("$failed", (int)DocumentStatus.Failed);
            if (await restore.ExecuteNonQueryAsync(cancellationToken) != 1) { await transaction.RollbackAsync(cancellationToken); return false; }
        }

        var requeue = connection.CreateCommand(); requeue.Transaction = transaction;
        requeue.CommandText = "UPDATE document_jobs SET state=$queued,attempt=0,not_before=$now,locked_at=NULL,last_error=NULL,updated_at=$now WHERE document_id=$document AND job_kind=$kind AND state=$terminal";
        requeue.Parameters.AddWithValue("$queued", (int)DocumentJobState.Queued); requeue.Parameters.AddWithValue("$now", now); requeue.Parameters.AddWithValue("$document", documentId.ToString()); requeue.Parameters.AddWithValue("$kind", (int)DocumentJobKind.CreateDatevPackage);
        requeue.Parameters.AddWithValue("$terminal", (int)jobState);
        if (await requeue.ExecuteNonQueryAsync(cancellationToken) != 1) { await transaction.RollbackAsync(cancellationToken); return false; }
        await DocumentStore.AddEventAsync(
            connection,
            transaction,
            documentId,
			retryFailedJob || restartBackoffAfterFix ? "DatevPackageRetryRequested" : "DatevPackageRebuildRequested",
			retryFailedJob
                ? "DATEV-Paketjob nach terminalem Fehler sicher erneut eingereiht."
				: restartBackoffAfterFix
					? "DATEV-Paketjob nach behobener technischer Ursache ohne weitere Wartezeit erneut eingereiht."
					: "Als abgeschlossen markierter DATEV-Paketjob ohne Paketnachweis sicher erneut eingereiht.",
            actor.Trim(),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<DocumentRecord> CreateDocumentAndEnqueueAsync(
        DocumentStore documents,
        SapDocumentIdentity sap,
        string pdfSha256,
        string originalFileName,
        string actor,
        DocumentJobKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var expectedKind = sap.Direction == DocumentDirection.Incoming ? DocumentJobKind.ValidateIncoming : DocumentJobKind.ValidateOutgoing;
        if (kind != expectedKind) throw new ArgumentException("Jobtyp und Dokumentrichtung stimmen nicht überein.", nameof(kind));

        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var document = await DocumentStore.CreateAsync(connection, transaction, sap, pdfSha256, originalFileName, actor, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO document_jobs(id,document_id,job_kind,state,attempt,not_before,created_at,updated_at) VALUES($id,$document,$kind,$state,0,$notBefore,$created,$updated)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); command.Parameters.AddWithValue("$document", document.Id.ToString());
        command.Parameters.AddWithValue("$kind", (int)kind); command.Parameters.AddWithValue("$state", (int)DocumentJobState.Queued);
        command.Parameters.AddWithValue("$notBefore", now.ToString("O")); command.Parameters.AddWithValue("$created", now.ToString("O")); command.Parameters.AddWithValue("$updated", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return document;
    }

    public async Task<DocumentJob?> ClaimNextAsync(DocumentJobKind? requestedKind = null, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var select = connection.CreateCommand(); select.Transaction = transaction; select.CommandText = "SELECT id,document_id,job_kind,attempt,not_before,last_error FROM document_jobs WHERE state=$queued AND not_before <= $now AND ($kind IS NULL OR job_kind=$kind) ORDER BY created_at LIMIT 1"; select.Parameters.AddWithValue("$queued", (int)DocumentJobState.Queued); select.Parameters.AddWithValue("$now", now.ToString("O")); select.Parameters.AddWithValue("$kind", requestedKind is null ? DBNull.Value : (object)(int)requestedKind.Value);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) { await transaction.CommitAsync(cancellationToken); return null; }
        var id = Guid.Parse(reader.GetString(0)); var documentId = Guid.Parse(reader.GetString(1)); var jobKind = (DocumentJobKind)reader.GetInt32(2); var attempt = reader.GetInt32(3) + 1; var notBefore = DateTimeOffset.Parse(reader.GetString(4)); var lastError = reader.IsDBNull(5) ? null : reader.GetString(5);
        await reader.DisposeAsync();
        var update = connection.CreateCommand(); update.Transaction = transaction; update.CommandText = "UPDATE document_jobs SET state=$running,attempt=$attempt,locked_at=$locked,updated_at=$updated WHERE id=$id AND state=$queued"; update.Parameters.AddWithValue("$running", (int)DocumentJobState.Running); update.Parameters.AddWithValue("$attempt", attempt); update.Parameters.AddWithValue("$locked", now.ToString("O")); update.Parameters.AddWithValue("$updated", now.ToString("O")); update.Parameters.AddWithValue("$id", id.ToString()); update.Parameters.AddWithValue("$queued", (int)DocumentJobState.Queued);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) { await transaction.RollbackAsync(cancellationToken); return null; }
        await transaction.CommitAsync(cancellationToken);
        return new(id, documentId, jobKind, DocumentJobState.Running, attempt, notBefore, lastError);
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken cancellationToken = default) => await SetFinalStateAsync(jobId, DocumentJobState.Completed, null, cancellationToken);

    public async Task<DocumentJobState> FailAsync(Guid jobId, string error, DocumentStore documents, string actor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(error)) throw new ArgumentException("Eine Fehlerbeschreibung ist erforderlich.", nameof(error));
        ArgumentNullException.ThrowIfNull(documents);
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Ein Akteur ist erforderlich.", nameof(actor));
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var lookup = connection.CreateCommand(); lookup.Transaction = transaction; lookup.CommandText = "SELECT document_id,job_kind,attempt FROM document_jobs WHERE id=$id AND state=$running"; lookup.Parameters.AddWithValue("$id", jobId.ToString()); lookup.Parameters.AddWithValue("$running", (int)DocumentJobState.Running);
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Nur laufende Jobs dürfen fehlschlagen.");
        var documentId = Guid.Parse(reader.GetString(0)); var jobKind = (DocumentJobKind)reader.GetInt32(1); var attempt = reader.GetInt32(2);
        await reader.DisposeAsync();
        var final = attempt >= MaximumAttempts;
        var now = DateTimeOffset.UtcNow; var retryAt = now.AddMinutes(Math.Pow(2, attempt));
        var safeError = DocumentStore.CreateSafeJobFailureDetail(jobKind, error);
        var state = final ? DocumentJobState.Failed : DocumentJobState.Queued;
        if (final && !await documents.TryMarkFailedAsync(connection, transaction, documentId, jobKind, safeError, actor.Trim(), cancellationToken))
            state = DocumentJobState.Completed; // Das fachliche Ergebnis wurde vor dem abgebrochenen Jobabschluss bereits dauerhaft gespeichert.
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE document_jobs SET state=$state,not_before=$notBefore,locked_at=NULL,last_error=$error,updated_at=$updated WHERE id=$id AND state=$running"; command.Parameters.AddWithValue("$state", (int)state); command.Parameters.AddWithValue("$notBefore", (final ? now : retryAt).ToString("O")); command.Parameters.AddWithValue("$error", state == DocumentJobState.Completed ? DBNull.Value : safeError); command.Parameters.AddWithValue("$updated", now.ToString("O")); command.Parameters.AddWithValue("$id", jobId.ToString()); command.Parameters.AddWithValue("$running", (int)DocumentJobState.Running);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Der Jobstatus wurde parallel geändert.");
        await transaction.CommitAsync(cancellationToken);
        return state;
    }

    public async Task<bool> RetryValidationAsync(Guid documentId, string reason, string actor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Für die erneute Validierung ist eine Begründung erforderlich.", nameof(reason));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Ein Akteur ist erforderlich.", nameof(actor));
        var detail = Regex.Replace(reason.Trim(), @"\s+", " ");
        if (detail.Length > 1000) throw new ArgumentException("Die Begründung darf höchstens 1000 Zeichen enthalten.", nameof(reason));

        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var lookup = connection.CreateCommand(); lookup.Transaction = transaction;
        lookup.CommandText = "SELECT direction,status FROM documents WHERE id=$id";
        lookup.Parameters.AddWithValue("$id", documentId.ToString());
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) { await transaction.RollbackAsync(cancellationToken); return false; }
        var direction = (NovaNein.Domain.DocumentDirection)reader.GetInt32(0);
        var status = (NovaNein.Domain.DocumentStatus)reader.GetInt32(1);
        await reader.DisposeAsync();
        if (status is not (NovaNein.Domain.DocumentStatus.NeedsReview or NovaNein.Domain.DocumentStatus.Rejected or NovaNein.Domain.DocumentStatus.Failed))
        { await transaction.RollbackAsync(cancellationToken); return false; }

        var kind = direction == NovaNein.Domain.DocumentDirection.Incoming ? DocumentJobKind.ValidateIncoming : DocumentJobKind.ValidateOutgoing;
        var requiredValidationState = status == NovaNein.Domain.DocumentStatus.Failed ? DocumentJobState.Failed : DocumentJobState.Completed;
        var now = DateTimeOffset.UtcNow;
        var requeue = connection.CreateCommand(); requeue.Transaction = transaction;
        requeue.CommandText = "UPDATE document_jobs SET state=$queued,attempt=0,not_before=$now,locked_at=NULL,last_error=NULL,updated_at=$now WHERE document_id=$document AND job_kind=$kind AND state=$required";
        requeue.Parameters.AddWithValue("$queued", (int)DocumentJobState.Queued); requeue.Parameters.AddWithValue("$now", now.ToString("O"));
        requeue.Parameters.AddWithValue("$document", documentId.ToString()); requeue.Parameters.AddWithValue("$kind", (int)kind);
        requeue.Parameters.AddWithValue("$required", (int)requiredValidationState);
        if (await requeue.ExecuteNonQueryAsync(cancellationToken) != 1) { await transaction.RollbackAsync(cancellationToken); return false; }

        var reset = connection.CreateCommand(); reset.Transaction = transaction;
        reset.CommandText = "UPDATE documents SET status=$received,signal=NULL,updated_at=$now WHERE id=$id AND status=$previous";
        reset.Parameters.AddWithValue("$received", (int)NovaNein.Domain.DocumentStatus.Received); reset.Parameters.AddWithValue("$now", now.ToString("O"));
        reset.Parameters.AddWithValue("$id", documentId.ToString()); reset.Parameters.AddWithValue("$previous", (int)status);
        if (await reset.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Der Belegstatus wurde parallel geändert.");
        await DocumentStore.AddEventAsync(connection, transaction, documentId, "ValidationRetryRequested", detail, actor.Trim(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Replaces the PDF of a document whose validation ended in a manual review or a terminal
    /// validation failure and atomically starts the matching validation job again. Documents
    /// that already reached DATEV packaging or transfer are intentionally excluded.
    /// </summary>
    public async Task<DocumentRecord?> ReplacePdfAndRetryValidationAsync(
        DocumentStore documents,
        Guid documentId,
        string pdfSha256,
        string originalFileName,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documentId == Guid.Empty) throw new ArgumentException("Eine Dokument-ID ist erforderlich.", nameof(documentId));
        if (string.IsNullOrWhiteSpace(pdfSha256) || pdfSha256.Length != 64 || !pdfSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Eine gültige PDF-Prüfsumme ist erforderlich.", nameof(pdfSha256));
        if (string.IsNullOrWhiteSpace(originalFileName)) throw new ArgumentException("Der PDF-Dateiname ist erforderlich.", nameof(originalFileName));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Ein Akteur ist erforderlich.", nameof(actor));

        var fileName = Path.GetFileName(originalFileName.Trim());
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Der PDF-Dateiname ist ungültig.", nameof(originalFileName));
        var normalizedHash = pdfSha256.ToUpperInvariant();

        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var lookup = connection.CreateCommand(); lookup.Transaction = transaction;
        lookup.CommandText = "SELECT direction,status,pdf_sha256,original_file_name FROM documents WHERE id=$id";
        lookup.Parameters.AddWithValue("$id", documentId.ToString());
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) { await transaction.RollbackAsync(cancellationToken); return null; }
        var direction = (DocumentDirection)reader.GetInt32(0);
        var status = (DocumentStatus)reader.GetInt32(1);
        var previousHash = reader.GetString(2);
        var previousFileName = reader.GetString(3);
        await reader.DisposeAsync();

        if (string.Equals(previousHash, normalizedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die ausgewählte PDF ist bereits mit diesem SAP-Beleg verknüpft.");
        if (status is not (DocumentStatus.NeedsReview or DocumentStatus.Rejected or DocumentStatus.Failed))
            throw new InvalidOperationException("Eine andere PDF darf nur nach einer gelben, roten oder endgültig fehlgeschlagenen Prüfung hochgeladen werden.");

        var packageTable = connection.CreateCommand(); packageTable.Transaction = transaction;
        packageTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='transfer_packages'";
        if (Convert.ToInt32(await packageTable.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            var packageEvidence = connection.CreateCommand(); packageEvidence.Transaction = transaction;
            packageEvidence.CommandText = "SELECT COUNT(*) FROM transfer_packages WHERE document_id=$id";
            packageEvidence.Parameters.AddWithValue("$id", documentId.ToString());
            if (Convert.ToInt32(await packageEvidence.ExecuteScalarAsync(cancellationToken)) > 0)
                throw new InvalidOperationException("Für diesen Beleg wurde bereits ein DATEV-Paket erzeugt. Die PDF darf nicht mehr ausgetauscht werden.");
        }

		var kind = direction == DocumentDirection.Incoming ? DocumentJobKind.ValidateIncoming : DocumentJobKind.ValidateOutgoing;
		bool packageFailure = false;
		if (status == DocumentStatus.Failed)
		{
			var failedPackage = connection.CreateCommand(); failedPackage.Transaction = transaction;
			failedPackage.CommandText = "SELECT COUNT(*) FROM document_jobs WHERE document_id=$document AND job_kind=$kind AND state=$failed";
			failedPackage.Parameters.AddWithValue("$document", documentId.ToString());
			failedPackage.Parameters.AddWithValue("$kind", (int)DocumentJobKind.CreateDatevPackage);
			failedPackage.Parameters.AddWithValue("$failed", (int)DocumentJobState.Failed);
			packageFailure = Convert.ToInt32(await failedPackage.ExecuteScalarAsync(cancellationToken)) == 1;
		}
		var requiredState = status == DocumentStatus.Failed && !packageFailure ? DocumentJobState.Failed : DocumentJobState.Completed;
        var now = DateTimeOffset.UtcNow;
        var requeue = connection.CreateCommand(); requeue.Transaction = transaction;
        requeue.CommandText = "UPDATE document_jobs SET state=$queued,attempt=0,not_before=$now,locked_at=NULL,last_error=NULL,updated_at=$now WHERE document_id=$document AND job_kind=$kind AND state=$required";
        requeue.Parameters.AddWithValue("$queued", (int)DocumentJobState.Queued); requeue.Parameters.AddWithValue("$now", now.ToString("O"));
        requeue.Parameters.AddWithValue("$document", documentId.ToString()); requeue.Parameters.AddWithValue("$kind", (int)kind);
        requeue.Parameters.AddWithValue("$required", (int)requiredState);
		if (await requeue.ExecuteNonQueryAsync(cancellationToken) != 1)
			throw new InvalidOperationException("Die PDF kann nur nach einem abgeschlossenen oder endgültig fehlgeschlagenen Validierungsjob ausgetauscht werden.");

		if (packageFailure)
		{
			var removeFailedPackage = connection.CreateCommand(); removeFailedPackage.Transaction = transaction;
			removeFailedPackage.CommandText = "DELETE FROM document_jobs WHERE document_id=$document AND job_kind=$kind AND state=$failed";
			removeFailedPackage.Parameters.AddWithValue("$document", documentId.ToString());
			removeFailedPackage.Parameters.AddWithValue("$kind", (int)DocumentJobKind.CreateDatevPackage);
			removeFailedPackage.Parameters.AddWithValue("$failed", (int)DocumentJobState.Failed);
			if (await removeFailedPackage.ExecuteNonQueryAsync(cancellationToken) != 1)
				throw new InvalidOperationException("Der fehlgeschlagene DATEV-Paketjob konnte nicht sicher zurückgesetzt werden.");
		}

        var replace = connection.CreateCommand(); replace.Transaction = transaction;
        replace.CommandText = "UPDATE documents SET pdf_sha256=$hash,original_file_name=$file,status=$received,signal=NULL,updated_at=$now WHERE id=$id AND status=$previous";
        replace.Parameters.AddWithValue("$hash", normalizedHash); replace.Parameters.AddWithValue("$file", fileName);
        replace.Parameters.AddWithValue("$received", (int)DocumentStatus.Received); replace.Parameters.AddWithValue("$now", now.ToString("O"));
        replace.Parameters.AddWithValue("$id", documentId.ToString()); replace.Parameters.AddWithValue("$previous", (int)status);
        if (await replace.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Der Belegstatus wurde parallel geändert.");

        await DocumentStore.AddEventAsync(
            connection,
            transaction,
            documentId,
            "PdfReplaced",
            $"PDF nach fehlgeschlagener oder manueller Prüfung ersetzt: {Path.GetFileName(previousFileName)} ({previousHash}) → {fileName} ({normalizedHash}). Validierung erneut eingereiht.",
            actor.Trim(),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await documents.GetAsync(documentId, cancellationToken);
    }

    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow; var command = connection.CreateCommand(); command.CommandText = "UPDATE document_jobs SET state=$queued,locked_at=NULL,last_error=COALESCE(last_error,'Dienstneustart während Verarbeitung.'),updated_at=$updated WHERE state=$running"; command.Parameters.AddWithValue("$queued", (int)DocumentJobState.Queued); command.Parameters.AddWithValue("$running", (int)DocumentJobState.Running); command.Parameters.AddWithValue("$updated", now.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Removes idempotent jobs of one kind so a controlled rebuild can enqueue them again.</summary>
    public async Task<int> ResetAsync(DocumentJobKind kind, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM document_jobs WHERE job_kind=$kind";
        command.Parameters.AddWithValue("$kind", (int)kind);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetFinalStateAsync(Guid jobId, DocumentJobState state, string? error, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow; var command = connection.CreateCommand(); command.CommandText = "UPDATE document_jobs SET state=$state,locked_at=NULL,last_error=$error,updated_at=$updated WHERE id=$id AND state=$running"; command.Parameters.AddWithValue("$state", (int)state); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value); command.Parameters.AddWithValue("$updated", now.ToString("O")); command.Parameters.AddWithValue("$id", jobId.ToString()); command.Parameters.AddWithValue("$running", (int)DocumentJobState.Running);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Nur laufende Jobs dürfen abgeschlossen werden.");
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            try { return numeric >= 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(numeric) : DateTimeOffset.FromUnixTimeSeconds(numeric); }
            catch (ArgumentOutOfRangeException) { }
        }
        throw new InvalidDataException("Der gespeicherte Jobzeitpunkt ist ungültig.");
    }
}
