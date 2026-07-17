using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NovaNein.Server;

public sealed class AutomaticBookingStore
{
    private readonly string _connectionString;

    public AutomaticBookingStore(IConfiguration configuration)
    {
        _connectionString = "Data Source=" + (configuration["Storage:DatabasePath"] ?? "data/novanein.db") + ";Default Timeout=5";
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var directory = Path.GetDirectoryName(dataSource);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS gmail_sync_state (
              mailbox TEXT PRIMARY KEY,
              history_id TEXT NULL,
              watch_expiration TEXT NULL,
              last_sync_at TEXT NULL,
              last_successful_sync_at TEXT NULL,
              last_error TEXT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mail_sources (
              id TEXT PRIMARY KEY,
              mailbox TEXT NOT NULL,
              gmail_message_id TEXT NOT NULL UNIQUE,
              gmail_thread_id TEXT NOT NULL,
              history_id TEXT NOT NULL,
              subject TEXT NOT NULL,
              sender TEXT NOT NULL,
              received_at TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              last_error TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS mail_attachments (
              id TEXT PRIMARY KEY,
              mail_source_id TEXT NOT NULL,
              gmail_attachment_id TEXT NOT NULL,
              file_name TEXT NOT NULL,
              mime_type TEXT NOT NULL,
              size INTEGER NOT NULL,
              sha256 TEXT NOT NULL,
              local_path TEXT NOT NULL,
              status TEXT NOT NULL,
              error TEXT NULL,
              FOREIGN KEY(mail_source_id) REFERENCES mail_sources(id),
              UNIQUE(mail_source_id,gmail_attachment_id),
              UNIQUE(sha256)
            );

            CREATE TABLE IF NOT EXISTS invoice_proposals (
              id TEXT PRIMARY KEY,
              mail_source_id TEXT NOT NULL,
              mail_attachment_id TEXT NOT NULL UNIQUE,
              version INTEGER NOT NULL,
              direction TEXT NOT NULL,
              status TEXT NOT NULL,
              signal TEXT NOT NULL,
              document_type TEXT NOT NULL,
              invoice_number TEXT NOT NULL,
              supplier_name TEXT NOT NULL,
              supplier_code TEXT NULL,
              supplier_vat_id TEXT NULL,
              supplier_tax_number TEXT NULL,
              supplier_iban TEXT NULL,
              invoice_date TEXT NOT NULL,
              service_date TEXT NULL,
              due_date TEXT NULL,
              net_amount TEXT NOT NULL,
              tax_amount TEXT NOT NULL,
              gross_amount TEXT NOT NULL,
              currency TEXT NOT NULL,
              has_purchase_order_reference INTEGER NOT NULL,
              has_goods_characteristics INTEGER NOT NULL,
              is_reverse_charge INTEGER NOT NULL,
              suggestion_reason TEXT NOT NULL,
              source_sha256 TEXT NOT NULL,
              findings_json TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              approved_by TEXT NULL,
              approved_at TEXT NULL,
              rejection_reason TEXT NULL,
              FOREIGN KEY(mail_source_id) REFERENCES mail_sources(id),
              FOREIGN KEY(mail_attachment_id) REFERENCES mail_attachments(id)
            );

            CREATE TABLE IF NOT EXISTS invoice_proposal_lines (
              proposal_id TEXT NOT NULL,
              line_number INTEGER NOT NULL,
              description TEXT NOT NULL,
              net_amount TEXT NOT NULL,
              tax_amount TEXT NOT NULL,
              tax_rate TEXT NULL,
              account TEXT NOT NULL,
              tax_code TEXT NOT NULL,
              suggestion_source TEXT NOT NULL,
              confidence TEXT NOT NULL,
              looks_like_goods INTEGER NOT NULL,
              PRIMARY KEY(proposal_id,line_number),
              FOREIGN KEY(proposal_id) REFERENCES invoice_proposals(id)
            );

            CREATE TABLE IF NOT EXISTS supplier_proposals (
              id TEXT PRIMARY KEY,
              invoice_proposal_id TEXT NOT NULL UNIQUE,
              version INTEGER NOT NULL,
              status TEXT NOT NULL,
              proposed_card_code TEXT NOT NULL,
              name TEXT NOT NULL,
              vat_id TEXT NULL,
              tax_number TEXT NULL,
              iban TEXT NULL,
              street TEXT NULL,
              postal_code TEXT NULL,
              city TEXT NULL,
              country_code TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              approved_by TEXT NULL,
              approved_at TEXT NULL,
              created_card_code TEXT NULL,
              last_error TEXT NULL,
              FOREIGN KEY(invoice_proposal_id) REFERENCES invoice_proposals(id)
            );

            CREATE TABLE IF NOT EXISTS sap_posting_results (
              invoice_proposal_id TEXT PRIMARY KEY,
              doc_entry INTEGER NOT NULL,
              doc_num INTEGER NOT NULL,
              trans_id INTEGER NOT NULL,
              attachment_entry INTEGER NOT NULL,
              readback_hash TEXT NOT NULL,
              posted_at TEXT NOT NULL,
              posted_by TEXT NOT NULL,
              FOREIGN KEY(invoice_proposal_id) REFERENCES invoice_proposals(id)
            );

            CREATE TABLE IF NOT EXISTS sap_orphan_attachments (
              attachment_entry INTEGER PRIMARY KEY,
              invoice_proposal_id TEXT NOT NULL,
              detected_at TEXT NOT NULL,
              error TEXT NOT NULL,
              resolved_at TEXT NULL,
              FOREIGN KEY(invoice_proposal_id) REFERENCES invoice_proposals(id)
            );

            CREATE TABLE IF NOT EXISTS automatic_booking_audit (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              proposal_id TEXT NULL,
              mail_source_id TEXT NULL,
              occurred_at TEXT NOT NULL,
              action TEXT NOT NULL,
              detail TEXT NOT NULL,
              actor TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_mail_sources_status_received ON mail_sources(status,received_at);
            CREATE INDEX IF NOT EXISTS ix_invoice_proposals_status_updated ON invoice_proposals(status,updated_at);
            CREATE INDEX IF NOT EXISTS ix_invoice_proposals_identity ON invoice_proposals(supplier_code,invoice_number,gross_amount);
            CREATE INDEX IF NOT EXISTS ix_supplier_proposals_status ON supplier_proposals(status,updated_at);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string? HistoryId, DateTimeOffset? WatchExpiration, DateTimeOffset? LastSyncAt, DateTimeOffset? LastSuccessfulSyncAt, string? LastError)> GetGmailStateAsync(string mailbox, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT history_id,watch_expiration,last_sync_at,last_successful_sync_at,last_error FROM gmail_sync_state WHERE mailbox=$mailbox";
        command.Parameters.AddWithValue("$mailbox", mailbox);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (null, null, null, null, null);
        return (
            ReadNullableString(reader, 0),
            ReadDateTime(reader, 1),
            ReadDateTime(reader, 2),
            ReadDateTime(reader, 3),
            ReadNullableString(reader, 4));
    }

    public async Task SaveGmailStateAsync(
        string mailbox,
        string? historyId,
        DateTimeOffset? watchExpiration,
        bool successful,
        string? error,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gmail_sync_state(mailbox,history_id,watch_expiration,last_sync_at,last_successful_sync_at,last_error,updated_at)
            VALUES($mailbox,$history,$expiration,$now,$success,$error,$now)
            ON CONFLICT(mailbox) DO UPDATE SET
              history_id=COALESCE($history,history_id),
              watch_expiration=COALESCE($expiration,watch_expiration),
              last_sync_at=$now,
              last_successful_sync_at=CASE WHEN $successful=1 THEN $now ELSE last_successful_sync_at END,
              last_error=$error,
              updated_at=$now
            """;
        command.Parameters.AddWithValue("$mailbox", mailbox);
        command.Parameters.AddWithValue("$history", (object?)historyId ?? DBNull.Value);
        command.Parameters.AddWithValue("$expiration", watchExpiration?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$success", successful ? now.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$successful", successful ? 1 : 0);
        command.Parameters.AddWithValue("$error", string.IsNullOrWhiteSpace(error) ? DBNull.Value : Safe(error));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> HasMailMessageAsync(string messageId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_sources WHERE gmail_message_id=$id";
        command.Parameters.AddWithValue("$id", messageId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    public async Task<bool> HasAttachmentHashAsync(string sha256, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_attachments WHERE sha256=$sha";
        command.Parameters.AddWithValue("$sha", sha256);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    public async Task<MailSourceRecord> CreateMailAsync(
        string mailbox,
        string messageId,
        string threadId,
        string historyId,
        string subject,
        string sender,
        DateTimeOffset receivedAt,
        IReadOnlyList<MailAttachmentRecord> attachments,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO mail_sources(id,mailbox,gmail_message_id,gmail_thread_id,history_id,subject,sender,received_at,status,created_at,updated_at,last_error)
                VALUES($id,$mailbox,$message,$thread,$history,$subject,$sender,$received,$status,$now,$now,NULL)
                """;
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.Parameters.AddWithValue("$mailbox", mailbox);
            insert.Parameters.AddWithValue("$message", messageId);
            insert.Parameters.AddWithValue("$thread", threadId);
            insert.Parameters.AddWithValue("$history", historyId);
            insert.Parameters.AddWithValue("$subject", Safe(subject, 500));
            insert.Parameters.AddWithValue("$sender", Safe(sender, 500));
            insert.Parameters.AddWithValue("$received", receivedAt.ToString("O"));
            insert.Parameters.AddWithValue("$status", MailSourceStatuses.MailReceived);
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);

            foreach (var source in attachments)
            {
                var attachment = source with { MailSourceId = id };
                var insertAttachment = connection.CreateCommand();
                insertAttachment.Transaction = (SqliteTransaction)transaction;
                insertAttachment.CommandText = """
                    INSERT INTO mail_attachments(id,mail_source_id,gmail_attachment_id,file_name,mime_type,size,sha256,local_path,status,error)
                    VALUES($id,$mail,$gmail,$name,$mime,$size,$sha,$path,$status,$error)
                    """;
                insertAttachment.Parameters.AddWithValue("$id", attachment.Id.ToString());
                insertAttachment.Parameters.AddWithValue("$mail", id.ToString());
                insertAttachment.Parameters.AddWithValue("$gmail", attachment.GmailAttachmentId);
                insertAttachment.Parameters.AddWithValue("$name", attachment.FileName);
                insertAttachment.Parameters.AddWithValue("$mime", attachment.MimeType);
                insertAttachment.Parameters.AddWithValue("$size", attachment.Size);
                insertAttachment.Parameters.AddWithValue("$sha", attachment.Sha256);
                insertAttachment.Parameters.AddWithValue("$path", attachment.LocalPath);
                insertAttachment.Parameters.AddWithValue("$status", attachment.Status);
                insertAttachment.Parameters.AddWithValue("$error", (object?)attachment.Error ?? DBNull.Value);
                await insertAttachment.ExecuteNonQueryAsync(ct);
            }

            await AddAuditAsync(connection, transaction, null, id, "MailImported", $"Gmail-Nachricht {messageId} mit {attachments.Count} Anhang/Anhängen übernommen.", "gmail-worker", ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
        return (await GetMailAsync(id, ct))!;
    }

    public async Task<MailSourceRecord?> GetMailAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,mailbox,gmail_message_id,gmail_thread_id,history_id,subject,sender,received_at,status,created_at,updated_at,last_error FROM mail_sources WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var mail = ReadMail(reader);
        await reader.DisposeAsync();
        return mail with { Attachments = await ReadAttachmentsAsync(connection, id, ct) };
    }

    public async Task<MailSourceRecord?> GetMailByMessageIdAsync(string messageId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM mail_sources WHERE gmail_message_id=$id";
        command.Parameters.AddWithValue("$id", messageId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is string id && Guid.TryParse(id, out var mailId)
            ? await GetMailAsync(mailId, ct)
            : null;
    }

    public async Task<bool> DeleteUnprocessedMailForRetryAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var proposalCheck = connection.CreateCommand();
        proposalCheck.Transaction = (SqliteTransaction)transaction;
        proposalCheck.CommandText = "SELECT COUNT(*) FROM invoice_proposals WHERE mail_source_id=$id";
        proposalCheck.Parameters.AddWithValue("$id", id.ToString());
        if (Convert.ToInt32(await proposalCheck.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        foreach (var statement in new[]
        {
            "DELETE FROM automatic_booking_audit WHERE mail_source_id=$id",
            "DELETE FROM mail_attachments WHERE mail_source_id=$id",
            "DELETE FROM mail_sources WHERE id=$id"
        })
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = statement;
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task SetMailStatusAsync(Guid id, string status, string? error, string actor, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE mail_sources SET status=$status,last_error=$error,updated_at=$now WHERE id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", string.IsNullOrWhiteSpace(error) ? DBNull.Value : Safe(error));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
        await AddAuditAsync(connection, transaction, null, id, status, error ?? status, actor, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<InvoiceProposal> SaveProposalAsync(
        InvoiceProposal proposal,
        SupplierProposal? supplierProposal,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var existingVersion = await ReadProposalVersionAsync(connection, transaction, proposal.Id, ct);
            var version = existingVersion.HasValue ? existingVersion.Value + 1 : Math.Max(1, proposal.Version);
            var now = DateTimeOffset.UtcNow;
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO invoice_proposals(
                  id,mail_source_id,mail_attachment_id,version,direction,status,signal,document_type,invoice_number,
                  supplier_name,supplier_code,supplier_vat_id,supplier_tax_number,supplier_iban,invoice_date,service_date,due_date,
                  net_amount,tax_amount,gross_amount,currency,has_purchase_order_reference,has_goods_characteristics,is_reverse_charge,
                  suggestion_reason,source_sha256,findings_json,created_at,updated_at,approved_by,approved_at,rejection_reason)
                VALUES(
                  $id,$mail,$attachment,$version,$direction,$status,$signal,$document,$invoice,$supplier,$supplierCode,$vat,$taxNumber,$iban,
                  $invoiceDate,$serviceDate,$dueDate,$net,$tax,$gross,$currency,$order,$goods,$reverse,$reason,$sha,$findings,$created,$updated,NULL,NULL,NULL)
                ON CONFLICT(id) DO UPDATE SET
                  version=$version,direction=$direction,status=$status,signal=$signal,document_type=$document,invoice_number=$invoice,
                  supplier_name=$supplier,supplier_code=$supplierCode,supplier_vat_id=$vat,supplier_tax_number=$taxNumber,supplier_iban=$iban,
                  invoice_date=$invoiceDate,service_date=$serviceDate,due_date=$dueDate,net_amount=$net,tax_amount=$tax,gross_amount=$gross,
                  currency=$currency,has_purchase_order_reference=$order,has_goods_characteristics=$goods,is_reverse_charge=$reverse,
                  suggestion_reason=$reason,findings_json=$findings,updated_at=$updated,approved_by=NULL,approved_at=NULL,rejection_reason=NULL
                """;
            AddProposalParameters(command, proposal with { Version = version }, now);
            await command.ExecuteNonQueryAsync(ct);

            var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM invoice_proposal_lines WHERE proposal_id=$id";
            delete.Parameters.AddWithValue("$id", proposal.Id.ToString());
            await delete.ExecuteNonQueryAsync(ct);
            foreach (var line in proposal.Lines) await InsertLineAsync(connection, transaction, proposal.Id, line, ct);

            if (supplierProposal != null)
                await UpsertSupplierProposalAsync(connection, transaction, supplierProposal with { InvoiceProposalId = proposal.Id }, ct);

            var mail = connection.CreateCommand();
            mail.Transaction = (SqliteTransaction)transaction;
            mail.CommandText = "UPDATE mail_sources SET status=$status,updated_at=$now,last_error=NULL WHERE id=$id";
            mail.Parameters.AddWithValue("$status", proposal.Status);
            mail.Parameters.AddWithValue("$now", now.ToString("O"));
            mail.Parameters.AddWithValue("$id", proposal.MailSourceId.ToString());
            await mail.ExecuteNonQueryAsync(ct);
            await AddAuditAsync(connection, transaction, proposal.Id, proposal.MailSourceId, existingVersion.HasValue ? "ProposalRecalculated" : "ProposalCreated", proposal.SuggestionReason, "proposal-engine", ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
        return (await GetProposalAsync(proposal.Id, ct))!;
    }

    public async Task<IReadOnlyList<InvoiceProposal>> ListProposalsAsync(string? status = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM invoice_proposals WHERE ($status IS NULL OR status=$status) ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(status) ? DBNull.Value : status);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(Guid.Parse(reader.GetString(0)));
        await reader.DisposeAsync();
        var result = new List<InvoiceProposal>(ids.Count);
        foreach (var id in ids)
        {
            var proposal = await GetProposalAsync(id, ct);
            if (proposal != null) result.Add(proposal);
        }
        return result;
    }

    public async Task<InvoiceProposal?> GetProposalAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,mail_source_id,mail_attachment_id,version,direction,status,signal,document_type,invoice_number,
                   supplier_name,supplier_code,supplier_vat_id,supplier_tax_number,supplier_iban,invoice_date,service_date,due_date,
                   net_amount,tax_amount,gross_amount,currency,has_purchase_order_reference,has_goods_characteristics,is_reverse_charge,
                   suggestion_reason,source_sha256,findings_json,created_at,updated_at,approved_by,approved_at,rejection_reason
            FROM invoice_proposals WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var proposal = ReadProposal(reader);
        await reader.DisposeAsync();
        var lines = await ReadProposalLinesAsync(connection, id, ct);
        var posting = await ReadPostingAsync(connection, id, ct);
        var mail = await GetMailAsync(proposal.MailSourceId, ct);
        return proposal with { Lines = lines, MailSource = mail, SapPosting = posting };
    }

    public async Task<InvoiceProposal> RejectProposalAsync(Guid id, int expectedVersion, string reason, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Eine Ablehnungsbegründung ist erforderlich.", nameof(reason));
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE invoice_proposals SET status=$status,rejection_reason=$reason,updated_at=$now,version=version+1
            WHERE id=$id AND version=$version AND status IN ($ready,$review,$blocked)
            """;
        command.Parameters.AddWithValue("$status", MailSourceStatuses.Rejected);
        command.Parameters.AddWithValue("$reason", Safe(reason));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$version", expectedVersion);
        command.Parameters.AddWithValue("$ready", MailSourceStatuses.ProposalReady);
        command.Parameters.AddWithValue("$review", MailSourceStatuses.NeedsReview);
        command.Parameters.AddWithValue("$blocked", MailSourceStatuses.Blocked);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Der Vorschlag wurde inzwischen geändert oder kann nicht mehr abgelehnt werden.");
        var mailId = await ReadProposalMailIdAsync(connection, transaction, id, ct);
        await UpdateMailInTransactionAsync(connection, transaction, mailId, MailSourceStatuses.Rejected, reason, now, ct);
        await AddAuditAsync(connection, transaction, id, mailId, "ProposalRejected", reason, actor, ct);
        await transaction.CommitAsync(ct);
        return (await GetProposalAsync(id, ct))!;
    }

    public async Task<InvoiceProposal> BeginPostingAsync(
        Guid id,
        int expectedVersion,
        string reason,
        IReadOnlyList<InvoiceProposalLineInput>? editedLines,
        string actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Eine Freigabebegründung ist erforderlich.", nameof(reason));
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE invoice_proposals
               SET status=$posting,approved_by=$actor,approved_at=$now,updated_at=$now,version=version+1
             WHERE id=$id AND version=$version AND status IN ($ready,$review)
            """;
        command.Parameters.AddWithValue("$posting", MailSourceStatuses.SapPosting);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$version", expectedVersion);
        command.Parameters.AddWithValue("$ready", MailSourceStatuses.ProposalReady);
        command.Parameters.AddWithValue("$review", MailSourceStatuses.NeedsReview);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Der Vorschlag wurde inzwischen geändert, ist blockiert oder wurde bereits verarbeitet.");

        if (editedLines is { Count: > 0 })
        {
            foreach (var line in editedLines)
            {
                var updateLine = connection.CreateCommand();
                updateLine.Transaction = (SqliteTransaction)transaction;
                updateLine.CommandText = """
                    UPDATE invoice_proposal_lines
                       SET account=$account,tax_code=$taxCode,suggestion_source='manual',confidence='1'
                     WHERE proposal_id=$proposal AND line_number=$line
                    """;
                updateLine.Parameters.AddWithValue("$account", line.Account.Trim());
                updateLine.Parameters.AddWithValue("$taxCode", line.TaxCode.Trim());
                updateLine.Parameters.AddWithValue("$proposal", id.ToString());
                updateLine.Parameters.AddWithValue("$line", line.LineNumber);
                if (await updateLine.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException($"Buchungszeile {line.LineNumber} wurde inzwischen geändert oder existiert nicht.");
            }
        }

        var mailId = await ReadProposalMailIdAsync(connection, transaction, id, ct);
        await UpdateMailInTransactionAsync(connection, transaction, mailId, MailSourceStatuses.SapPosting, null, now, ct);
        await AddAuditAsync(connection, transaction, id, mailId, "ProposalApprovedForSap", reason, actor, ct);
        await transaction.CommitAsync(ct);
        return (await GetProposalAsync(id, ct))!;
    }

    public async Task RecordPostingAsync(SapPostingResult result, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO sap_posting_results(invoice_proposal_id,doc_entry,doc_num,trans_id,attachment_entry,readback_hash,posted_at,posted_by)
            VALUES($id,$entry,$num,$trans,$attachment,$hash,$at,$by)
            ON CONFLICT(invoice_proposal_id) DO UPDATE SET
              doc_entry=$entry,doc_num=$num,trans_id=$trans,attachment_entry=$attachment,readback_hash=$hash,posted_at=$at,posted_by=$by
            """;
        insert.Parameters.AddWithValue("$id", result.InvoiceProposalId.ToString());
        insert.Parameters.AddWithValue("$entry", result.DocEntry);
        insert.Parameters.AddWithValue("$num", result.DocNum);
        insert.Parameters.AddWithValue("$trans", result.TransId);
        insert.Parameters.AddWithValue("$attachment", result.AttachmentEntry);
        insert.Parameters.AddWithValue("$hash", result.ReadbackHash);
        insert.Parameters.AddWithValue("$at", result.PostedAt.ToString("O"));
        insert.Parameters.AddWithValue("$by", result.PostedBy);
        await insert.ExecuteNonQueryAsync(ct);

        var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = "UPDATE invoice_proposals SET status=$status,updated_at=$now WHERE id=$id AND status=$posting";
        update.Parameters.AddWithValue("$status", MailSourceStatuses.SapReadbackConfirmed);
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", result.InvoiceProposalId.ToString());
        update.Parameters.AddWithValue("$posting", MailSourceStatuses.SapPosting);
        if (await update.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Der Buchungsvorschlag ist nicht mehr im erwarteten SAP-Buchungsstatus.");
        var mailId = await ReadProposalMailIdAsync(connection, transaction, result.InvoiceProposalId, ct);
        await UpdateMailInTransactionAsync(connection, transaction, mailId, MailSourceStatuses.SapReadbackConfirmed, null, DateTimeOffset.UtcNow, ct);
        await AddAuditAsync(connection, transaction, result.InvoiceProposalId, mailId, "SapReadbackConfirmed", $"SAP {result.DocNum}, DocEntry {result.DocEntry}, TransId {result.TransId}.", result.PostedBy, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task MarkPostingFailedAsync(Guid id, string error, string actor, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE invoice_proposals SET status=$failed,signal='red',suggestion_reason=$error,updated_at=$now WHERE id=$id AND status=$posting";
        command.Parameters.AddWithValue("$failed", MailSourceStatuses.Failed);
        command.Parameters.AddWithValue("$error", Safe(error));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$posting", MailSourceStatuses.SapPosting);
        await command.ExecuteNonQueryAsync(ct);
        var mailId = await ReadProposalMailIdAsync(connection, transaction, id, ct);
        await UpdateMailInTransactionAsync(connection, transaction, mailId, MailSourceStatuses.Failed, error, now, ct);
        await AddAuditAsync(connection, transaction, id, mailId, "SapPostingFailed", error, actor, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task SetDownstreamStatusAsync(Guid id, string status, string actor, CancellationToken ct = default)
    {
        if (status is not (MailSourceStatuses.DatevPrepared or MailSourceStatuses.DatevFinalized))
            throw new ArgumentOutOfRangeException(nameof(status));
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE invoice_proposals
               SET status=$status,updated_at=$now
             WHERE id=$id
               AND (($status=$prepared AND status=$readback)
                 OR ($status=$finalized AND status IN ($readback,$prepared)))
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$prepared", MailSourceStatuses.DatevPrepared);
        command.Parameters.AddWithValue("$finalized", MailSourceStatuses.DatevFinalized);
        command.Parameters.AddWithValue("$readback", MailSourceStatuses.SapReadbackConfirmed);
        if (await command.ExecuteNonQueryAsync(ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return;
        }
        var mailId = await ReadProposalMailIdAsync(connection, transaction, id, ct);
        await UpdateMailInTransactionAsync(connection, transaction, mailId, status, null, DateTimeOffset.UtcNow, ct);
        await AddAuditAsync(connection, transaction, id, mailId, status, status, actor, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<SupplierProposal>> ListSupplierProposalsAsync(string? status = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,invoice_proposal_id,version,status,proposed_card_code,name,vat_id,tax_number,iban,street,postal_code,city,country_code,
                   created_at,updated_at,approved_by,approved_at,created_card_code,last_error
              FROM supplier_proposals
             WHERE ($status IS NULL OR status=$status)
             ORDER BY updated_at DESC
            """;
        command.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(status) ? DBNull.Value : status);
        var result = new List<SupplierProposal>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadSupplierProposal(reader));
        return result;
    }

    public async Task<SupplierProposal?> GetSupplierProposalAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,invoice_proposal_id,version,status,proposed_card_code,name,vat_id,tax_number,iban,street,postal_code,city,country_code,
                   created_at,updated_at,approved_by,approved_at,created_card_code,last_error
              FROM supplier_proposals WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSupplierProposal(reader) : null;
    }

    public async Task<SupplierProposal> BeginSupplierCreationAsync(Guid id, int expectedVersion, string cardCode, string reason, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Eine Stammdatenbegründung ist erforderlich.", nameof(reason));
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE supplier_proposals
               SET status='Creating',proposed_card_code=$card,approved_by=$actor,approved_at=$now,updated_at=$now,version=version+1,last_error=NULL
             WHERE id=$id AND version=$version AND status IN ('Proposed','Failed')
            """;
        command.Parameters.AddWithValue("$card", cardCode);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Der Lieferantenvorschlag wurde inzwischen geändert oder verarbeitet.");
        return (await GetSupplierProposalAsync(id, ct))!;
    }

    public async Task CompleteSupplierCreationAsync(Guid id, string createdCardCode, string actor, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var supplier = await ReadSupplierProposalAsync(connection, transaction, id, ct)
            ?? throw new KeyNotFoundException("Der Lieferantenvorschlag wurde nicht gefunden.");
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE supplier_proposals SET status='Created',created_card_code=$card,updated_at=$now,last_error=NULL WHERE id=$id AND status='Creating'";
        command.Parameters.AddWithValue("$card", createdCardCode);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Der Lieferantenvorschlag ist nicht mehr im erwarteten Anlagezustand.");
        var proposal = connection.CreateCommand();
        proposal.Transaction = (SqliteTransaction)transaction;
        proposal.CommandText = "UPDATE invoice_proposals SET supplier_code=$card,status=$status,signal='yellow',updated_at=$now,version=version+1 WHERE id=$id";
        proposal.Parameters.AddWithValue("$card", createdCardCode);
        proposal.Parameters.AddWithValue("$status", MailSourceStatuses.NeedsReview);
        proposal.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        proposal.Parameters.AddWithValue("$id", supplier.InvoiceProposalId.ToString());
        await proposal.ExecuteNonQueryAsync(ct);
        var mailId = await ReadProposalMailIdAsync(connection, transaction, supplier.InvoiceProposalId, ct);
        await UpdateMailInTransactionAsync(connection, transaction, mailId, MailSourceStatuses.NeedsReview, null, DateTimeOffset.UtcNow, ct);
        await AddAuditAsync(connection, transaction, supplier.InvoiceProposalId, mailId, "SupplierCreated", $"SAP-Lieferant {createdCardCode} wurde aus dem getrennt freigegebenen Stammdatensatz angelegt.", actor, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task FailSupplierCreationAsync(Guid id, string error, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE supplier_proposals SET status='Failed',last_error=$error,updated_at=$now WHERE id=$id AND status='Creating'";
        command.Parameters.AddWithValue("$error", Safe(error));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordOrphanAttachmentAsync(Guid proposalId, int attachmentEntry, string error, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sap_orphan_attachments(attachment_entry,invoice_proposal_id,detected_at,error,resolved_at)
            VALUES($entry,$proposal,$at,$error,NULL)
            ON CONFLICT(attachment_entry) DO UPDATE SET error=$error,resolved_at=NULL
            """;
        command.Parameters.AddWithValue("$entry", attachmentEntry);
        command.Parameters.AddWithValue("$proposal", proposalId.ToString());
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$error", Safe(error));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<(int OpenProposals, int FailedMessages, int OrphanAttachments)> CountsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM invoice_proposals WHERE status IN ('ProposalReady','NeedsReview','Blocked','SapPosting')),
              (SELECT COUNT(*) FROM mail_sources WHERE status='Failed'),
              (SELECT COUNT(*) FROM sap_orphan_attachments WHERE resolved_at IS NULL)
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public async Task<bool> HasPotentialDuplicateAsync(string? supplierCode, string invoiceNumber, decimal grossAmount, Guid? exceptProposalId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber)) return false;
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM invoice_proposals
             WHERE invoice_number=$invoice
               AND gross_amount=$gross
               AND ($supplier IS NULL OR supplier_code=$supplier)
               AND ($except IS NULL OR id<>$except)
               AND status NOT IN ('Rejected','Failed')
            """;
        command.Parameters.AddWithValue("$invoice", invoiceNumber.Trim());
        command.Parameters.AddWithValue("$gross", Decimal(grossAmount));
        command.Parameters.AddWithValue("$supplier", string.IsNullOrWhiteSpace(supplierCode) ? DBNull.Value : supplierCode);
        command.Parameters.AddWithValue("$except", exceptProposalId?.ToString() ?? (object)DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    public async Task<bool> AreAllMailProposalsResolvedAsync(Guid mailSourceId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
              WHEN COUNT(*) > 0
               AND SUM(CASE WHEN status IN ('SapReadbackConfirmed','DatevPrepared','DatevFinalized','Rejected') THEN 1 ELSE 0 END) = COUNT(*)
              THEN 1 ELSE 0 END
              FROM invoice_proposals
             WHERE mail_source_id=$mail
            """;
        command.Parameters.AddWithValue("$mail", mailSourceId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private static void AddProposalParameters(SqliteCommand command, InvoiceProposal proposal, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", proposal.Id.ToString());
        command.Parameters.AddWithValue("$mail", proposal.MailSourceId.ToString());
        command.Parameters.AddWithValue("$attachment", proposal.MailAttachmentId.ToString());
        command.Parameters.AddWithValue("$version", proposal.Version);
        command.Parameters.AddWithValue("$direction", proposal.Direction);
        command.Parameters.AddWithValue("$status", proposal.Status);
        command.Parameters.AddWithValue("$signal", proposal.Signal);
        command.Parameters.AddWithValue("$document", proposal.DocumentType);
        command.Parameters.AddWithValue("$invoice", proposal.InvoiceNumber);
        command.Parameters.AddWithValue("$supplier", proposal.SupplierName);
        command.Parameters.AddWithValue("$supplierCode", (object?)proposal.SupplierCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$vat", (object?)proposal.SupplierVatId ?? DBNull.Value);
        command.Parameters.AddWithValue("$taxNumber", (object?)proposal.SupplierTaxNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$iban", (object?)proposal.SupplierIban ?? DBNull.Value);
        command.Parameters.AddWithValue("$invoiceDate", proposal.InvoiceDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$serviceDate", proposal.ServiceDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$dueDate", proposal.DueDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$net", Decimal(proposal.NetAmount));
        command.Parameters.AddWithValue("$tax", Decimal(proposal.TaxAmount));
        command.Parameters.AddWithValue("$gross", Decimal(proposal.GrossAmount));
        command.Parameters.AddWithValue("$currency", proposal.Currency);
        command.Parameters.AddWithValue("$order", proposal.HasPurchaseOrderReference ? 1 : 0);
        command.Parameters.AddWithValue("$goods", proposal.HasGoodsCharacteristics ? 1 : 0);
        command.Parameters.AddWithValue("$reverse", proposal.IsReverseCharge ? 1 : 0);
        command.Parameters.AddWithValue("$reason", Safe(proposal.SuggestionReason, 2000));
        command.Parameters.AddWithValue("$sha", proposal.SourceSha256);
        command.Parameters.AddWithValue("$findings", JsonSerializer.Serialize(proposal.Findings));
        command.Parameters.AddWithValue("$created", proposal.CreatedAt == default ? now.ToString("O") : proposal.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
    }

    private static async Task InsertLineAsync(SqliteConnection connection, DbTransaction transaction, Guid proposalId, InvoiceProposalLine line, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO invoice_proposal_lines(proposal_id,line_number,description,net_amount,tax_amount,tax_rate,account,tax_code,suggestion_source,confidence,looks_like_goods)
            VALUES($proposal,$line,$description,$net,$tax,$rate,$account,$taxCode,$source,$confidence,$goods)
            """;
        command.Parameters.AddWithValue("$proposal", proposalId.ToString());
        command.Parameters.AddWithValue("$line", line.LineNumber);
        command.Parameters.AddWithValue("$description", Safe(line.Description, 500));
        command.Parameters.AddWithValue("$net", Decimal(line.NetAmount));
        command.Parameters.AddWithValue("$tax", Decimal(line.TaxAmount));
        command.Parameters.AddWithValue("$rate", line.TaxRate.HasValue ? Decimal(line.TaxRate.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$account", line.Account);
        command.Parameters.AddWithValue("$taxCode", line.TaxCode);
        command.Parameters.AddWithValue("$source", line.SuggestionSource);
        command.Parameters.AddWithValue("$confidence", Decimal(line.Confidence));
        command.Parameters.AddWithValue("$goods", line.LooksLikeGoods ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertSupplierProposalAsync(SqliteConnection connection, DbTransaction transaction, SupplierProposal supplier, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO supplier_proposals(id,invoice_proposal_id,version,status,proposed_card_code,name,vat_id,tax_number,iban,street,postal_code,city,country_code,created_at,updated_at,approved_by,approved_at,created_card_code,last_error)
            VALUES($id,$proposal,$version,$status,$card,$name,$vat,$tax,$iban,$street,$zip,$city,$country,$created,$updated,NULL,NULL,NULL,NULL)
            ON CONFLICT(invoice_proposal_id) DO UPDATE SET
              version=version+1,status=CASE WHEN status='Created' THEN status ELSE 'Proposed' END,
              proposed_card_code=$card,name=$name,vat_id=$vat,tax_number=$tax,iban=$iban,street=$street,postal_code=$zip,city=$city,country_code=$country,
              updated_at=$updated,last_error=NULL
            """;
        command.Parameters.AddWithValue("$id", supplier.Id.ToString());
        command.Parameters.AddWithValue("$proposal", supplier.InvoiceProposalId.ToString());
        command.Parameters.AddWithValue("$version", Math.Max(1, supplier.Version));
        command.Parameters.AddWithValue("$status", supplier.Status);
        command.Parameters.AddWithValue("$card", supplier.ProposedCardCode);
        command.Parameters.AddWithValue("$name", supplier.Name);
        command.Parameters.AddWithValue("$vat", (object?)supplier.VatId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tax", (object?)supplier.TaxNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$iban", (object?)supplier.Iban ?? DBNull.Value);
        command.Parameters.AddWithValue("$street", (object?)supplier.Street ?? DBNull.Value);
        command.Parameters.AddWithValue("$zip", (object?)supplier.PostalCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$city", (object?)supplier.City ?? DBNull.Value);
        command.Parameters.AddWithValue("$country", supplier.CountryCode);
        command.Parameters.AddWithValue("$created", supplier.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", supplier.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int?> ReadProposalVersionAsync(SqliteConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT version FROM invoice_proposals WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        var value = await command.ExecuteScalarAsync(ct);
        return value == null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<Guid> ReadProposalMailIdAsync(SqliteConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT mail_source_id FROM invoice_proposals WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        var value = await command.ExecuteScalarAsync(ct) as string;
        return value == null ? throw new KeyNotFoundException("Der Buchungsvorschlag wurde nicht gefunden.") : Guid.Parse(value);
    }

    private static async Task UpdateMailInTransactionAsync(SqliteConnection connection, DbTransaction transaction, Guid mailId, string status, string? error, DateTimeOffset now, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE mail_sources SET status=$status,last_error=$error,updated_at=$now WHERE id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", string.IsNullOrWhiteSpace(error) ? DBNull.Value : Safe(error));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", mailId.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddAuditAsync(SqliteConnection connection, DbTransaction transaction, Guid? proposalId, Guid? mailId, string action, string detail, string actor, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO automatic_booking_audit(proposal_id,mail_source_id,occurred_at,action,detail,actor) VALUES($proposal,$mail,$at,$action,$detail,$actor)";
        command.Parameters.AddWithValue("$proposal", proposalId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$mail", mailId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$detail", Safe(detail, 2000));
        command.Parameters.AddWithValue("$actor", actor);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<MailAttachmentRecord>> ReadAttachmentsAsync(SqliteConnection connection, Guid mailId, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,mail_source_id,gmail_attachment_id,file_name,mime_type,size,sha256,local_path,status,error FROM mail_attachments WHERE mail_source_id=$id ORDER BY file_name";
        command.Parameters.AddWithValue("$id", mailId.ToString());
        var result = new List<MailAttachmentRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new MailAttachmentRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                ReadNullableString(reader, 9)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<InvoiceProposalLine>> ReadProposalLinesAsync(SqliteConnection connection, Guid id, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT line_number,description,net_amount,tax_amount,tax_rate,account,tax_code,suggestion_source,confidence,looks_like_goods FROM invoice_proposal_lines WHERE proposal_id=$id ORDER BY line_number";
        command.Parameters.AddWithValue("$id", id.ToString());
        var result = new List<InvoiceProposalLine>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new InvoiceProposalLine(
                reader.GetInt32(0),
                reader.GetString(1),
                ReadDecimal(reader, 2),
                ReadDecimal(reader, 3),
                reader.IsDBNull(4) ? null : ReadDecimal(reader, 4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                ReadDecimal(reader, 8),
                reader.GetInt32(9) != 0));
        }
        return result;
    }

    private static async Task<SapPostingResult?> ReadPostingAsync(SqliteConnection connection, Guid id, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT invoice_proposal_id,doc_entry,doc_num,trans_id,attachment_entry,readback_hash,posted_at,posted_by FROM sap_posting_results WHERE invoice_proposal_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new SapPostingResult(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)), reader.GetString(7));
    }

    private static async Task<SupplierProposal?> ReadSupplierProposalAsync(SqliteConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT id,invoice_proposal_id,version,status,proposed_card_code,name,vat_id,tax_number,iban,street,postal_code,city,country_code,
                   created_at,updated_at,approved_by,approved_at,created_card_code,last_error
              FROM supplier_proposals WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSupplierProposal(reader) : null;
    }

    private static MailSourceRecord ReadMail(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        DateTimeOffset.Parse(reader.GetString(7)),
        reader.GetString(8),
        DateTimeOffset.Parse(reader.GetString(9)),
        DateTimeOffset.Parse(reader.GetString(10)),
        ReadNullableString(reader, 11));

    private static InvoiceProposal ReadProposal(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        Guid.Parse(reader.GetString(2)),
        reader.GetInt32(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        ReadNullableString(reader, 10),
        ReadNullableString(reader, 11),
        ReadNullableString(reader, 12),
        ReadNullableString(reader, 13),
        DateOnly.Parse(reader.GetString(14)),
        ReadDateOnly(reader, 15),
        ReadDateOnly(reader, 16),
        ReadDecimal(reader, 17),
        ReadDecimal(reader, 18),
        ReadDecimal(reader, 19),
        reader.GetString(20),
        reader.GetInt32(21) != 0,
        reader.GetInt32(22) != 0,
        reader.GetInt32(23) != 0,
        reader.GetString(24),
        reader.GetString(25),
        DateTimeOffset.Parse(reader.GetString(27)),
        DateTimeOffset.Parse(reader.GetString(28)),
        ReadNullableString(reader, 29),
        ReadDateTime(reader, 30),
        ReadNullableString(reader, 31),
        JsonSerializer.Deserialize<string[]>(reader.GetString(26)) ?? [],
        []);

    private static SupplierProposal ReadSupplierProposal(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        ReadNullableString(reader, 6),
        ReadNullableString(reader, 7),
        ReadNullableString(reader, 8),
        ReadNullableString(reader, 9),
        ReadNullableString(reader, 10),
        ReadNullableString(reader, 11),
        reader.GetString(12),
        DateTimeOffset.Parse(reader.GetString(13)),
        DateTimeOffset.Parse(reader.GetString(14)),
        ReadNullableString(reader, 15),
        ReadDateTime(reader, 16),
        ReadNullableString(reader, 17),
        ReadNullableString(reader, 18));

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private static string Safe(string value, int maximum = 1000)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }

    private static string Decimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal ReadDecimal(SqliteDataReader reader, int ordinal) => decimal.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateOnly? ReadDateOnly(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateOnly.Parse(reader.GetString(ordinal));
    private static DateTimeOffset? ReadDateTime(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
}
