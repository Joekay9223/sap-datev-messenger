using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class DocumentStore
{
	private const int MaximumFailureDetailLength = 320;

	private readonly string _connectionString;

	public DocumentStore(IConfiguration configuration)
	{
		_connectionString = "Data Source=" + (configuration["Storage:DatabasePath"] ?? "data/novanein.db");
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		string directory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(_connectionString).DataSource);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}
		SqliteConnection connection = new SqliteConnection(_connectionString);
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "CREATE TABLE IF NOT EXISTS documents (\n  id TEXT PRIMARY KEY, direction INTEGER NOT NULL, doc_entry INTEGER NOT NULL, doc_num INTEGER NOT NULL, sap_kind INTEGER NOT NULL DEFAULT 0,\n  pdf_sha256 TEXT NOT NULL UNIQUE, original_file_name TEXT NOT NULL, status INTEGER NOT NULL,\n  signal INTEGER NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,\n  UNIQUE(direction, sap_kind, doc_entry)\n);\nCREATE TABLE IF NOT EXISTS audit_events (\n  id INTEGER PRIMARY KEY AUTOINCREMENT, document_id TEXT NOT NULL, occurred_at TEXT NOT NULL,\n  kind TEXT NOT NULL, detail TEXT NOT NULL, actor TEXT NOT NULL\n);";
			await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
			SqliteCommand columns = connection.CreateCommand();
			((DbCommand)(object)columns).CommandText = "PRAGMA table_info(documents)";
			bool hasSapKind = false;
			SqliteDataReader reader = await columns.ExecuteReaderAsync(cancellationToken);
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					if (string.Equals(((DbDataReader)(object)reader).GetString(1), "sap_kind", StringComparison.OrdinalIgnoreCase))
					{
						hasSapKind = true;
					}
				}
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			if (!hasSapKind)
			{
				SqliteCommand migrate = connection.CreateCommand();
				((DbCommand)(object)migrate).CommandText = "ALTER TABLE documents ADD COLUMN sap_kind INTEGER NOT NULL DEFAULT 0";
				await ((DbCommand)(object)migrate).ExecuteNonQueryAsync(cancellationToken);
			}
			SqliteCommand index = connection.CreateCommand();
			((DbCommand)(object)index).CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_documents_sap_kind_entry ON documents(direction,sap_kind,doc_entry)";
			await ((DbCommand)(object)index).ExecuteNonQueryAsync(cancellationToken);
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
	}

	public async Task<DocumentRecord> CreateAsync(SapDocumentIdentity sap, string hash, string fileName, string actor, CancellationToken cancellationToken = default(CancellationToken))
	{
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DocumentRecord result2;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteTransaction transaction = connection.BeginTransaction();
			try
			{
				DocumentRecord result = await CreateAsync(connection, transaction, sap, hash, fileName, actor, cancellationToken);
				await ((DbTransaction)(object)transaction).CommitAsync(cancellationToken);
				result2 = result;
			}
			finally
			{
				((IDisposable)transaction)?.Dispose();
			}
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result2;
	}

	internal static async Task<DocumentRecord> CreateAsync(SqliteConnection connection, SqliteTransaction transaction, SapDocumentIdentity sap, string hash, string fileName, string actor, CancellationToken cancellationToken)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		DocumentRecord result = new DocumentRecord(Guid.NewGuid(), sap, hash, fileName, DocumentStatus.Received, null, now, now);
		SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		((DbCommand)(object)command).CommandText = "INSERT INTO documents(id,direction,doc_entry,doc_num,sap_kind,pdf_sha256,original_file_name,status,signal,created_at,updated_at) VALUES ($id,$direction,$entry,$num,$kind,$hash,$name,$status,NULL,$created,$updated);";
		command.Parameters.AddWithValue("$id", (object)result.Id.ToString());
		command.Parameters.AddWithValue("$direction", (object)(int)sap.Direction);
		command.Parameters.AddWithValue("$entry", (object)sap.DocEntry);
		command.Parameters.AddWithValue("$num", (object)sap.DocNum);
		command.Parameters.AddWithValue("$kind", (object)(int)sap.Type);
		command.Parameters.AddWithValue("$hash", (object)hash);
		command.Parameters.AddWithValue("$name", (object)fileName);
		command.Parameters.AddWithValue("$status", (object)(int)result.Status);
		command.Parameters.AddWithValue("$created", (object)now.ToString("O"));
		command.Parameters.AddWithValue("$updated", (object)now.ToString("O"));
		await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
		await AddEventAsync(connection, transaction, result.Id, "DocumentReceived", "PDF in geschützten Prüfbereich übernommen.", actor, cancellationToken);
		return result;
	}

	public async Task<DocumentRecord?> RecordValidationAsync(Guid id, ValidationResult result, string actor, CancellationToken cancellationToken = default(CancellationToken))
	{
		DocumentStatus status = result.Signal switch
		{
			ReviewSignal.Green => DocumentStatus.Approved,
			ReviewSignal.Yellow => DocumentStatus.NeedsReview,
			_ => DocumentStatus.Rejected,
		};
		DateTimeOffset now = DateTimeOffset.UtcNow;
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DocumentRecord result2;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteTransaction transaction = connection.BeginTransaction();
			try
			{
				SqliteCommand command = connection.CreateCommand();
				command.Transaction = transaction;
				((DbCommand)(object)command).CommandText = "UPDATE documents SET status=$status, signal=$signal, updated_at=$updated WHERE id=$id AND status=$received";
				command.Parameters.AddWithValue("$id", (object)id.ToString());
				command.Parameters.AddWithValue("$status", (object)(int)status);
				command.Parameters.AddWithValue("$signal", (object)(int)result.Signal);
				command.Parameters.AddWithValue("$updated", (object)now.ToString("O"));
				command.Parameters.AddWithValue("$received", (object)0);
				if (await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken) != 1)
				{
					await ((DbTransaction)(object)transaction).RollbackAsync(cancellationToken);
					result2 = null;
				}
				else
				{
					await AddEventAsync(connection, transaction, id, "ValidationCompleted", string.Join(" ", result.Reasons.DefaultIfEmpty("PDF und SAP stimmen überein.")), actor, cancellationToken);
					await ((DbTransaction)(object)transaction).CommitAsync(cancellationToken);
					result2 = await GetAsync(id, cancellationToken);
				}
			}
			finally
			{
				((IDisposable)transaction)?.Dispose();
			}
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result2;
	}

	public async Task<DocumentRecord?> ReviewAsync(Guid id, bool approve, string reason, string actor, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(reason))
		{
			throw new ArgumentException("Für jede manuelle Freigabe oder Ablehnung ist eine Begründung erforderlich.", "reason");
		}
		DocumentStatus status = (approve ? DocumentStatus.Approved : DocumentStatus.Rejected);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DocumentRecord result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteTransaction transaction = connection.BeginTransaction();
			try
			{
				SqliteCommand command = connection.CreateCommand();
				command.Transaction = transaction;
				((DbCommand)(object)command).CommandText = "UPDATE documents SET status=$status, updated_at=$updated WHERE id=$id AND (status=$review OR ($approve=1 AND status=$rejected))";
				command.Parameters.AddWithValue("$id", (object)id.ToString());
				command.Parameters.AddWithValue("$status", (object)(int)status);
				command.Parameters.AddWithValue("$updated", (object)now.ToString("O"));
				command.Parameters.AddWithValue("$review", (object)2);
				command.Parameters.AddWithValue("$rejected", (object)3);
				command.Parameters.AddWithValue("$approve", approve ? 1 : 0);
				if (await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken) != 1)
				{
					await ((DbTransaction)(object)transaction).RollbackAsync(cancellationToken);
					result = null;
				}
				else
				{
					await AddEventAsync(connection, transaction, id, approve ? "ManualReviewApproved" : "ManualReviewRejected", reason.Trim(), actor, cancellationToken);
					await ((DbTransaction)(object)transaction).CommitAsync(cancellationToken);
					result = await GetAsync(id, cancellationToken);
				}
			}
			finally
			{
				((IDisposable)transaction)?.Dispose();
			}
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<bool> RecordCreditNoteDatevReleaseAsync(Guid id, string reason, string actor, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(reason))
			throw new ArgumentException("Für die DATEV-Freigabe einer Gutschrift ist eine Begründung erforderlich.", nameof(reason));

		await using var connection = new SqliteConnection(_connectionString);
		await connection.OpenAsync(cancellationToken);
		await using var transaction = connection.BeginTransaction();

		var eligible = connection.CreateCommand();
		eligible.Transaction = transaction;
		eligible.CommandText = """
			SELECT COUNT(*) FROM documents
			WHERE id=$id
			  AND sap_kind IN ($purchaseCreditNote,$creditNote)
			  AND status IN ($approved,$attached,$packaged,$transferred)
			""";
		eligible.Parameters.AddWithValue("$id", id.ToString());
		eligible.Parameters.AddWithValue("$purchaseCreditNote", (int)SapBusinessDocumentType.PurchaseCreditNote);
		eligible.Parameters.AddWithValue("$creditNote", (int)SapBusinessDocumentType.CreditNote);
		eligible.Parameters.AddWithValue("$approved", (int)DocumentStatus.Approved);
		eligible.Parameters.AddWithValue("$attached", (int)DocumentStatus.AttachedToSap);
		eligible.Parameters.AddWithValue("$packaged", (int)DocumentStatus.Packaged);
		eligible.Parameters.AddWithValue("$transferred", (int)DocumentStatus.Transferred);
		if (Convert.ToInt32(await eligible.ExecuteScalarAsync(cancellationToken)) != 1)
		{
			await transaction.RollbackAsync(cancellationToken);
			return false;
		}

		var existing = connection.CreateCommand();
		existing.Transaction = transaction;
		existing.CommandText = "SELECT COUNT(*) FROM audit_events WHERE document_id=$id AND kind='CreditNoteDatevReleaseApproved'";
		existing.Parameters.AddWithValue("$id", id.ToString());
		if (Convert.ToInt32(await existing.ExecuteScalarAsync(cancellationToken)) == 0)
			await AddEventAsync(connection, transaction, id, "CreditNoteDatevReleaseApproved", reason.Trim(), actor, cancellationToken);

		await transaction.CommitAsync(cancellationToken);
		return true;
	}

	public async Task<bool> HasCreditNoteDatevReleaseAsync(Guid id, CancellationToken cancellationToken = default)
	{
		await using var connection = new SqliteConnection(_connectionString);
		await connection.OpenAsync(cancellationToken);
		var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE document_id=$id AND kind='CreditNoteDatevReleaseApproved'";
		command.Parameters.AddWithValue("$id", id.ToString());
		return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
	}

	public async Task<DocumentRecord?> MarkAttachedToSapAsync(Guid id, string actor, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE documents SET status=$status, updated_at=$updated WHERE id=$id AND status=$approved";
        command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$status", (int)DocumentStatus.AttachedToSap); command.Parameters.AddWithValue("$updated", now.ToString("O")); command.Parameters.AddWithValue("$approved", (int)DocumentStatus.Approved);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            var existing = connection.CreateCommand(); existing.Transaction = transaction;
            existing.CommandText = "SELECT status FROM documents WHERE id=$id"; existing.Parameters.AddWithValue("$id", id.ToString());
            var current = await existing.ExecuteScalarAsync(cancellationToken);
            if (current is null) { await transaction.RollbackAsync(cancellationToken); return null; }
            if ((DocumentStatus)Convert.ToInt32(current) != DocumentStatus.Packaged) { await transaction.RollbackAsync(cancellationToken); return null; }
            await AddEventAsync(connection, transaction, id, "SapAttachmentVerified", "SAP Attachments2-Anlage und Belegverknüpfung wurden durch Readback bestätigt; der bereits erreichte DATEV-Paketstatus bleibt erhalten.", actor, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(id, cancellationToken);
        }
        await AddEventAsync(connection, transaction, id, "SapAttachmentVerified", "SAP Attachments2-Anlage und Belegverknüpfung wurden durch Readback bestätigt.", actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

	public async Task<DocumentRecord?> RecordDatevPackagePreparedAsync(Guid id, string packagePath, string packageSha256, DateTimeOffset preparedAt, string actor, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Eine Dokument-ID ist erforderlich.", nameof(id));
        if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(packageSha256)) throw new ArgumentException("DATEV-Paketpfad und Prüfsumme sind erforderlich.");
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var exists = connection.CreateCommand(); exists.Transaction = transaction; exists.CommandText = "SELECT status FROM documents WHERE id=$id"; exists.Parameters.AddWithValue("$id", id.ToString());
        var statusValue = await exists.ExecuteScalarAsync(cancellationToken);
        if (statusValue is null) throw new KeyNotFoundException("Der Beleg zum DATEV-Paket fehlt.");
        var status = (DocumentStatus)Convert.ToInt32(statusValue);
        if (status is not (DocumentStatus.Approved or DocumentStatus.AttachedToSap or DocumentStatus.Packaged))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        if (status is DocumentStatus.Approved or DocumentStatus.AttachedToSap)
        {
            var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE documents SET status=$packaged, updated_at=$updated WHERE id=$id AND status IN ($approved,$attached)";
            update.Parameters.AddWithValue("$packaged", (int)DocumentStatus.Packaged); update.Parameters.AddWithValue("$updated", preparedAt.ToString("O")); update.Parameters.AddWithValue("$id", id.ToString());
            update.Parameters.AddWithValue("$approved", (int)DocumentStatus.Approved); update.Parameters.AddWithValue("$attached", (int)DocumentStatus.AttachedToSap);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) { await transaction.RollbackAsync(cancellationToken); return null; }
        }
        await AddEventAsync(connection, transaction, id, "DatevPackagePrepared", $"DATEV-ZIP vorbereitet: {Path.GetFileName(packagePath)} ({packageSha256.ToUpperInvariant()}) am {preparedAt:O}.", actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

	internal async Task<bool> TryMarkFailedAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, DocumentJobKind jobKind, string safeDetail, string actor, CancellationToken cancellationToken)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		DocumentStatus documentStatus;
		switch (jobKind)
		{
		case DocumentJobKind.ValidateIncoming:
		case DocumentJobKind.ValidateOutgoing:
			documentStatus = DocumentStatus.Received;
			break;
		case DocumentJobKind.AttachToSap:
			documentStatus = DocumentStatus.Approved;
			break;
		case DocumentJobKind.CreateDatevPackage:
			documentStatus = DocumentStatus.Approved;
			break;
		default:
			throw new ArgumentOutOfRangeException("jobKind", "Für diesen Jobtyp ist kein terminaler Dokumentfehler definiert.");
		}
		DocumentStatus expectedStatus = documentStatus;
		SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		((DbCommand)(object)command).CommandText = ((jobKind == DocumentJobKind.CreateDatevPackage) ? "UPDATE documents SET status=$failed, updated_at=$updated WHERE id=$id AND status IN ($expected,$attached)" : "UPDATE documents SET status=$failed, updated_at=$updated WHERE id=$id AND status=$expected");
		command.Parameters.AddWithValue("$id", (object)id.ToString());
		command.Parameters.AddWithValue("$failed", (object)8);
		command.Parameters.AddWithValue("$expected", (object)(int)expectedStatus);
		command.Parameters.AddWithValue("$updated", (object)now.ToString("O"));
		if (jobKind == DocumentJobKind.CreateDatevPackage)
		{
			command.Parameters.AddWithValue("$attached", (object)5);
		}
		if (await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken) != 1)
		{
			SqliteCommand exists = connection.CreateCommand();
			exists.Transaction = transaction;
			((DbCommand)(object)exists).CommandText = "SELECT COUNT(*) FROM documents WHERE id=$id";
			exists.Parameters.AddWithValue("$id", (object)id.ToString());
			if (Convert.ToInt32(await ((DbCommand)(object)exists).ExecuteScalarAsync(cancellationToken)) != 1)
			{
				throw new InvalidOperationException("Der Beleg zum endgültig fehlgeschlagenen Job fehlt.");
			}
			return false;
		}
		await AddEventAsync(connection, transaction, id, "DocumentJobFailed", safeDetail, actor, cancellationToken);
		return true;
	}

	internal static string CreateSafeJobFailureDetail(DocumentJobKind jobKind, string error)
	{
		if (error.Length > 2048)
		{
			error = error.Substring(0, 2048);
		}
		string detail = Regex.Replace(error, "(?i)\\b(password|passwort|token|secret|api[-_ ]?key|authorization)\\s*[:=]\\s*[^\\s;,]+", "$1=[GESCHÜTZT]");
		detail = Regex.Replace(detail, "(?i)\\b(?:bearer|basic)\\s+[A-Za-z0-9+/=._-]+", "[GESCHÜTZT]");
		detail = Regex.Replace(detail, "(?i)\\b(?:sk|rk|pk)-[A-Za-z0-9_-]{8,}", "[GESCHÜTZT]");
		detail = Regex.Replace(detail, "https?://\\S+", "[URL]");
		detail = Regex.Replace(detail, "(?:[A-Za-z]:\\\\|\\\\\\\\)(?:(?:[^\\\\\\r\\n]+\\\\)+)?[^\\s,;]*", "[PFAD]");
		detail = Regex.Replace(detail, "\\s+", " ").Trim();
		if (string.IsNullOrEmpty(detail))
		{
			detail = "Keine sichere Fehlerbeschreibung verfügbar.";
		}
		string prefix = $"{jobKind} endgültig fehlgeschlagen: ";
		if (detail.Length > 320 - prefix.Length)
		{
			detail = detail.Substring(0, 320 - prefix.Length - 1).TrimEnd() + "…";
		}
		return prefix + detail;
	}

	public async Task<DocumentRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default(CancellationToken))
	{
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DocumentRecord result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT * FROM documents WHERE id=$id";
			command.Parameters.AddWithValue("$id", (object)id.ToString());
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			DocumentRecord documentRecord;
			try
			{
				documentRecord = ((await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)) ? ReadDocument(reader) : null);
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = documentRecord;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<DocumentRecord?> GetBySapAsync(DocumentDirection direction, int docEntry, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (docEntry <= 0)
		{
			throw new ArgumentOutOfRangeException("docEntry");
		}
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DocumentRecord result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT * FROM documents WHERE direction=$direction AND doc_entry=$entry";
			command.Parameters.AddWithValue("$direction", (object)(int)direction);
			command.Parameters.AddWithValue("$entry", (object)docEntry);
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			DocumentRecord documentRecord;
			try
			{
				documentRecord = ((await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)) ? ReadDocument(reader) : null);
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = documentRecord;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<DocumentRecord?> GetBySapAsync(DocumentDirection direction, SapBusinessDocumentType type, int docEntry, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (docEntry <= 0)
		{
			throw new ArgumentOutOfRangeException("docEntry");
		}
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DocumentRecord result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT * FROM documents WHERE direction=$direction AND sap_kind=$kind AND doc_entry=$entry";
			command.Parameters.AddWithValue("$direction", (object)(int)direction);
			command.Parameters.AddWithValue("$kind", (object)(int)type);
			command.Parameters.AddWithValue("$entry", (object)docEntry);
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			DocumentRecord documentRecord;
			try
			{
				documentRecord = ((await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)) ? ReadDocument(reader) : null);
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = documentRecord;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<IReadOnlySet<string>> ListPdfHashesAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		HashSet<string> hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlySet<string> result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT pdf_sha256 FROM documents";
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlySet<string> readOnlySet;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					hashes.Add(((DbDataReader)(object)reader).GetString(0));
				}
				readOnlySet = hashes;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = readOnlySet;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<IReadOnlyList<DocumentRecord>> ListReceivedAsync(DocumentDirection? direction = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		List<DocumentRecord> documents = new List<DocumentRecord>();
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlyList<DocumentRecord> result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT * FROM documents WHERE status=$status AND ($direction IS NULL OR direction=$direction) ORDER BY created_at";
			command.Parameters.AddWithValue("$status", (object)0);
			command.Parameters.AddWithValue("$direction", (!direction.HasValue) ? DBNull.Value : ((object)(int)direction.Value));
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<DocumentRecord> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					documents.Add(ReadDocument(reader));
				}
				readOnlyList = documents;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = readOnlyList;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<IReadOnlyList<DocumentRecord>> ListApprovedAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		List<DocumentRecord> documents = new List<DocumentRecord>();
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlyList<DocumentRecord> result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT * FROM documents WHERE status=$status ORDER BY created_at";
			command.Parameters.AddWithValue("$status", (object)4);
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<DocumentRecord> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					documents.Add(ReadDocument(reader));
				}
				readOnlyList = documents;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = readOnlyList;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<IReadOnlyList<DocumentRecord>> ListAttachedToSapAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		List<DocumentRecord> documents = new List<DocumentRecord>();
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlyList<DocumentRecord> result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT * FROM documents WHERE status=$status ORDER BY created_at";
			command.Parameters.AddWithValue("$status", (object)5);
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<DocumentRecord> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					documents.Add(ReadDocument(reader));
				}
				readOnlyList = documents;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = readOnlyList;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<DocumentStatistics> StatisticsAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DocumentStatistics result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT\n  COUNT(*),\n  COALESCE(SUM(CASE WHEN status=$received THEN 1 ELSE 0 END),0),\n  COALESCE(SUM(CASE WHEN status=$review THEN 1 ELSE 0 END),0),\n  COALESCE(SUM(CASE WHEN status=$approved THEN 1 ELSE 0 END),0),\n  COALESCE(SUM(CASE WHEN status=$rejected THEN 1 ELSE 0 END),0),\n  COALESCE(SUM(CASE WHEN status=$failed THEN 1 ELSE 0 END),0),\n  COALESCE(SUM(CASE WHEN status=$attached THEN 1 ELSE 0 END),0),\n  COALESCE(SUM(CASE WHEN created_at >= $last7 THEN 1 ELSE 0 END),0),\n  COALESCE(SUM(CASE WHEN created_at >= $last30 THEN 1 ELSE 0 END),0)\nFROM documents;";
			command.Parameters.AddWithValue("$received", (object)0);
			command.Parameters.AddWithValue("$review", (object)2);
			command.Parameters.AddWithValue("$approved", (object)4);
			command.Parameters.AddWithValue("$rejected", (object)3);
			command.Parameters.AddWithValue("$failed", (object)8);
			command.Parameters.AddWithValue("$attached", (object)5);
			command.Parameters.AddWithValue("$last7", (object)DateTimeOffset.UtcNow.AddDays(-7.0).ToString("O"));
			command.Parameters.AddWithValue("$last30", (object)DateTimeOffset.UtcNow.AddDays(-30.0).ToString("O"));
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			DocumentStatistics documentStatistics;
			try
			{
				if (!(await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)))
				{
					throw new InvalidOperationException("Die NovaNein-Statistik konnte nicht gelesen werden.");
				}
				documentStatistics = new DocumentStatistics(Convert.ToInt32(((DbDataReader)(object)reader).GetInt64(0)), Convert.ToInt32(((DbDataReader)(object)reader).GetInt64(1)), Convert.ToInt32(((DbDataReader)(object)reader).GetInt64(2)), Convert.ToInt32(((DbDataReader)(object)reader).GetInt64(3)), Convert.ToInt32(((DbDataReader)(object)reader).GetInt64(4)), Convert.ToInt32(((DbDataReader)(object)reader).GetInt64(5)), Convert.ToInt32(((DbDataReader)(object)reader).GetInt64(6)), Convert.ToInt32(((DbDataReader)(object)reader).GetInt64(7)), Convert.ToInt32(((DbDataReader)(object)reader).GetInt64(8)));
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = documentStatistics;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<UploadPeriodStatistics> UploadStatisticsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
	{
		if (to < from)
		{
			throw new ArgumentException("Der Endtag darf nicht vor dem Starttag liegen.");
		}

		DateTimeOffset start = StartOfLocalDayUtc(from);
		DateTimeOffset endExclusive = StartOfLocalDayUtc(to.AddDays(1));
		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			SELECT
			  COUNT(*),
			  COALESCE(SUM(CASE WHEN direction=$incoming THEN 1 ELSE 0 END),0),
			  COALESCE(SUM(CASE WHEN direction=$outgoing THEN 1 ELSE 0 END),0)
			FROM documents
			WHERE created_at >= $start AND created_at < $end;
			""";
		command.Parameters.AddWithValue("$incoming", (int)DocumentDirection.Incoming);
		command.Parameters.AddWithValue("$outgoing", (int)DocumentDirection.Outgoing);
		command.Parameters.AddWithValue("$start", start.ToString("O"));
		command.Parameters.AddWithValue("$end", endExclusive.ToString("O"));
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		if (!await reader.ReadAsync(cancellationToken))
		{
			throw new InvalidOperationException("Die Uploadstatistik konnte nicht gelesen werden.");
		}

		return new UploadPeriodStatistics(
			Convert.ToInt32(reader.GetInt64(0)),
			Convert.ToInt32(reader.GetInt64(1)),
			Convert.ToInt32(reader.GetInt64(2)));
	}

	private static DateTimeOffset StartOfLocalDayUtc(DateOnly date)
	{
		DateTime local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
		TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(local);
		return new DateTimeOffset(local, offset).ToUniversalTime();
	}

	public async Task<IReadOnlyList<AuditEvent>> EventsAsync(Guid id, CancellationToken cancellationToken = default(CancellationToken))
	{
		List<AuditEvent> events = new List<AuditEvent>();
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlyList<AuditEvent> result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT * FROM audit_events WHERE document_id=$id ORDER BY id";
			command.Parameters.AddWithValue("$id", (object)id.ToString());
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<AuditEvent> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					events.Add(new AuditEvent(id, DateTimeOffset.Parse(((DbDataReader)(object)reader).GetString(2)), ((DbDataReader)(object)reader).GetString(3), ((DbDataReader)(object)reader).GetString(4), ((DbDataReader)(object)reader).GetString(5)));
				}
				readOnlyList = events;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = readOnlyList;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<IReadOnlyList<DocumentActivity>> RecentActivityAsync(int limit = 50, CancellationToken cancellationToken = default(CancellationToken))
	{
		limit = Math.Clamp(limit, 1, 100);
		List<DocumentActivity> activity = new List<DocumentActivity>();
		await using SqliteConnection connection = new SqliteConnection(_connectionString);
		await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
		SqliteCommand command = connection.CreateCommand();
		((DbCommand)(object)command).CommandText = """
			SELECT a.document_id, a.occurred_at, a.kind, a.detail, a.actor,
			       d.direction, d.doc_entry, d.doc_num, d.sap_kind, d.original_file_name, d.status, d.signal
			FROM audit_events a
			INNER JOIN documents d ON d.id = a.document_id
			ORDER BY a.id DESC
			LIMIT $limit
			""";
		command.Parameters.AddWithValue("$limit", limit);
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
		{
			activity.Add(new DocumentActivity(
				Guid.Parse(((DbDataReader)(object)reader).GetString(0)),
				DateTimeOffset.Parse(((DbDataReader)(object)reader).GetString(1)),
				((DbDataReader)(object)reader).GetString(2),
				((DbDataReader)(object)reader).GetString(3),
				((DbDataReader)(object)reader).GetString(4),
				(DocumentDirection)((DbDataReader)(object)reader).GetInt32(5),
				((DbDataReader)(object)reader).GetInt32(6),
				((DbDataReader)(object)reader).GetInt32(7),
				(SapBusinessDocumentType)((DbDataReader)(object)reader).GetInt32(8),
				((DbDataReader)(object)reader).GetString(9),
				(DocumentStatus)((DbDataReader)(object)reader).GetInt32(10),
				((DbDataReader)(object)reader).IsDBNull(11) ? null : (ReviewSignal?)((DbDataReader)(object)reader).GetInt32(11)));
		}
		return activity;
	}

	public async Task BackupDatabaseAsync(string targetPath, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathFullyQualified(targetPath))
		{
			throw new ArgumentException("Der Sicherungspfad muss absolut sein.", "targetPath");
		}
		string directory = Path.GetDirectoryName(targetPath) ?? throw new ArgumentException("Der Sicherungspfad benötigt einen Ordner.", "targetPath");
		Directory.CreateDirectory(directory);
		string temporary = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
		SqliteConnection source = new SqliteConnection(_connectionString);
		try
		{
			await ((DbConnection)(object)source).OpenAsync(cancellationToken);
			SqliteConnection destination = new SqliteConnection(((object)new SqliteConnectionStringBuilder
			{
				DataSource = temporary,
				Pooling = false
			}).ToString());
			try
			{
				await ((DbConnection)(object)destination).OpenAsync(cancellationToken);
				source.BackupDatabase(destination);
			}
			finally
			{
				if (destination != null)
				{
					await ((DbConnection)(object)destination).DisposeAsync();
				}
			}
			File.Move(temporary, targetPath, overwrite: true);
		}
		finally
		{
			if (source != null)
			{
				await ((DbConnection)(object)source).DisposeAsync();
			}
		}
	}

	internal static async Task AddEventAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, string kind, string detail, string actor, CancellationToken ct)
	{
		SqliteCommand cmd = connection.CreateCommand();
		cmd.Transaction = transaction;
		((DbCommand)(object)cmd).CommandText = "INSERT INTO audit_events(document_id,occurred_at,kind,detail,actor) VALUES($id,$at,$kind,$detail,$actor)";
		cmd.Parameters.AddWithValue("$id", (object)id.ToString());
		cmd.Parameters.AddWithValue("$at", (object)DateTimeOffset.UtcNow.ToString("O"));
		cmd.Parameters.AddWithValue("$kind", (object)kind);
		cmd.Parameters.AddWithValue("$detail", (object)detail);
		cmd.Parameters.AddWithValue("$actor", (object)actor);
		await ((DbCommand)(object)cmd).ExecuteNonQueryAsync(ct);
	}

	private static DocumentRecord ReadDocument(SqliteDataReader r)
	{
		int typeOrdinal = ((DbDataReader)(object)r).GetOrdinal("sap_kind");
		return new DocumentRecord(Guid.Parse(((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("id"))), new SapDocumentIdentity((DocumentDirection)((DbDataReader)(object)r).GetInt32(((DbDataReader)(object)r).GetOrdinal("direction")), ((DbDataReader)(object)r).GetInt32(((DbDataReader)(object)r).GetOrdinal("doc_entry")), ((DbDataReader)(object)r).GetInt32(((DbDataReader)(object)r).GetOrdinal("doc_num")), (!((DbDataReader)(object)r).IsDBNull(typeOrdinal)) ? ((SapBusinessDocumentType)((DbDataReader)(object)r).GetInt32(typeOrdinal)) : SapBusinessDocumentType.Unspecified), ((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("pdf_sha256")), ((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("original_file_name")), (DocumentStatus)((DbDataReader)(object)r).GetInt32(((DbDataReader)(object)r).GetOrdinal("status")), ((DbDataReader)(object)r).IsDBNull(((DbDataReader)(object)r).GetOrdinal("signal")) ? ((ReviewSignal?)null) : new ReviewSignal?((ReviewSignal)((DbDataReader)(object)r).GetInt32(((DbDataReader)(object)r).GetOrdinal("signal"))), DateTimeOffset.Parse(((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("created_at"))), DateTimeOffset.Parse(((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("updated_at"))));
	}
}
