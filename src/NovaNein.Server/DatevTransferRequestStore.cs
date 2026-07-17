using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class DatevTransferRequestStore
{
	private readonly string _connectionString;

	private readonly TransferEvidenceStore _evidence;

	public DatevTransferRequestStore(IConfiguration configuration, TransferEvidenceStore evidence)
	{
		_evidence = evidence;
		_connectionString = "Data Source=" + (configuration["Storage:DatabasePath"] ?? "data/novanein.db");
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		string source = new SqliteConnectionStringBuilder(_connectionString).DataSource;
		string directory = Path.GetDirectoryName(source);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}
		SqliteConnection connection = new SqliteConnection(_connectionString);
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "CREATE TABLE IF NOT EXISTS datev_transfer_requests (\n  id TEXT PRIMARY KEY,\n  document_id TEXT NOT NULL UNIQUE,\n  package_sha256 TEXT NOT NULL,\n  status TEXT NOT NULL,\n  requested_at TEXT NOT NULL,\n  requested_by TEXT NOT NULL,\n  attempts INTEGER NOT NULL DEFAULT 0,\n  last_error TEXT NULL\n);\nCREATE INDEX IF NOT EXISTS ix_datev_transfer_requests_status ON datev_transfer_requests(status, requested_at);";
			await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
			await EnsureColumnAsync(connection, "bridge_staged_at", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "watchfolder_delivered_at", "TEXT NULL", cancellationToken);
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
	}

	public async Task<DatevTransferRequest> RequestAsync(Guid documentId, string packageSha256, string requestedBy, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (documentId == Guid.Empty)
		{
			throw new ArgumentException("Eine Dokument-ID ist erforderlich.", "documentId");
		}
		if (string.IsNullOrWhiteSpace(requestedBy))
		{
			throw new ArgumentException("Ein Benutzer ist erforderlich.", "requestedBy");
		}
		string hash = NormalizeHash(packageSha256);
		TransferEvidence registered = (await _evidence.GetAsync(documentId, cancellationToken)) ?? throw new InvalidOperationException("Für diesen Beleg wurde noch kein DATEV-Paket vorbereitet.");
		if (!string.Equals(registered.PackageSha256, hash, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Die angegebene DATEV-Prüfsumme stimmt nicht mit dem vorbereiteten Paket überein.");
		}
		DatevTransferRequest existing = await GetByDocumentAsync(documentId, cancellationToken);
		if (existing != null)
		{
			if (!string.Equals(existing.PackageSha256, hash, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Für diesen Beleg existiert bereits ein hashgebundener DATEV-Transferauftrag.");
			return existing;
		}
		DateTimeOffset now = DateTimeOffset.UtcNow;
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DatevTransferRequest result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "INSERT OR IGNORE INTO datev_transfer_requests(id,document_id,package_sha256,status,requested_at,requested_by,attempts,last_error,bridge_staged_at,watchfolder_delivered_at)\nVALUES($id,$document,$hash,'queued',$at,$by,0,NULL,NULL,NULL);";
			command.Parameters.AddWithValue("$id", (object)Guid.NewGuid().ToString());
			command.Parameters.AddWithValue("$document", (object)documentId.ToString());
			command.Parameters.AddWithValue("$hash", (object)hash);
			command.Parameters.AddWithValue("$at", (object)now.ToString("O"));
			command.Parameters.AddWithValue("$by", (object)requestedBy.Trim());
			await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
			result = (await GetByDocumentAsync(documentId, cancellationToken)) ?? throw new InvalidOperationException("Der DATEV-Transferauftrag konnte nicht gelesen werden.");
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

	public async Task<DatevTransferRequest?> GetByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default(CancellationToken))
	{
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DatevTransferRequest result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT id,document_id,package_sha256,status,requested_at,requested_by,attempts,last_error,bridge_staged_at,watchfolder_delivered_at FROM datev_transfer_requests WHERE document_id=$document";
			command.Parameters.AddWithValue("$document", (object)documentId.ToString());
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			DatevTransferRequest datevTransferRequest;
			try
			{
				datevTransferRequest = ((await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)) ? Read(reader) : null);
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = datevTransferRequest;
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

	public async Task<IReadOnlyList<DatevTransferRequest>> ListAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		List<DatevTransferRequest> result = new List<DatevTransferRequest>();
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlyList<DatevTransferRequest> result2;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT id,document_id,package_sha256,status,requested_at,requested_by,attempts,last_error,bridge_staged_at,watchfolder_delivered_at FROM datev_transfer_requests";
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<DatevTransferRequest> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					result.Add(Read(reader));
				}
				readOnlyList = result;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result2 = readOnlyList;
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

	public async Task<int> EnsureGreenPackagesQueuedAsync(DateTimeOffset notBefore, string requestedBy, CancellationToken cancellationToken = default(CancellationToken))
		=> await EnsureInvoicePackagesQueuedAsync(notBefore, requestedBy, requireGreenSignal: true, cancellationToken);

	public async Task<int> EnsureApprovedInvoicePackagesQueuedAsync(DateTimeOffset notBefore, string requestedBy, CancellationToken cancellationToken = default(CancellationToken))
		=> await EnsureInvoicePackagesQueuedAsync(notBefore, requestedBy, requireGreenSignal: false, cancellationToken);

	private async Task<int> EnsureInvoicePackagesQueuedAsync(DateTimeOffset notBefore, string requestedBy, bool requireGreenSignal, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(requestedBy))
		{
			throw new ArgumentException("Ein technischer Akteur ist erforderlich.", nameof(requestedBy));
		}
		List<(Guid DocumentId, string PackageSha256)> eligible = new List<(Guid, string)>();
		await using (SqliteConnection connection = new SqliteConnection(_connectionString))
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = """
				SELECT d.id,p.zip_sha256
				FROM documents d
				JOIN transfer_packages p ON p.document_id=d.id
				LEFT JOIN datev_transfer_requests r ON r.document_id=d.id
				WHERE d.status=$packaged
				  AND d.sap_kind IN ($purchaseInvoice,$invoice)
				  AND p.package_prepared_at IS NOT NULL AND p.package_prepared_at >= $notBefore
				  AND r.id IS NULL
				""" + (requireGreenSignal ? " AND d.signal=$green" : string.Empty)
				+ " ORDER BY p.package_prepared_at,d.id";
			command.Parameters.AddWithValue("$packaged", (int)DocumentStatus.Packaged);
			command.Parameters.AddWithValue("$green", (int)ReviewSignal.Green);
			command.Parameters.AddWithValue("$purchaseInvoice", (int)SapBusinessDocumentType.PurchaseInvoice);
			command.Parameters.AddWithValue("$invoice", (int)SapBusinessDocumentType.Invoice);
			command.Parameters.AddWithValue("$notBefore", notBefore.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
			{
				eligible.Add((Guid.Parse(((DbDataReader)(object)reader).GetString(0)), ((DbDataReader)(object)reader).GetString(1)));
			}
		}
		int queued = 0;
		foreach ((Guid documentId, string packageSha256) in eligible)
		{
			DatevTransferRequest? existing = await GetByDocumentAsync(documentId, cancellationToken);
			if (existing is not null) continue;
			await RequestAsync(documentId, packageSha256, requestedBy, cancellationToken);
			queued++;
		}
		return queued;
	}

	public async Task<DatevTransferRequest?> ClaimNextAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DatevTransferRequest result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			DatevTransferRequest datevTransferRequest;
			await using (DbTransaction transaction = await ((DbConnection)(object)connection).BeginTransactionAsync(cancellationToken))
			{
				SqliteCommand select = connection.CreateCommand();
				select.Transaction = (SqliteTransaction)transaction;
				((DbCommand)(object)select).CommandText = "SELECT id FROM datev_transfer_requests WHERE status='queued' AND attempts < 5 ORDER BY requested_at LIMIT 1";
				object idValue = await ((DbCommand)(object)select).ExecuteScalarAsync(cancellationToken);
				if (idValue == null || idValue is DBNull)
				{
					await transaction.CommitAsync(cancellationToken);
					datevTransferRequest = null;
				}
				else
				{
					Guid id = Guid.Parse(Convert.ToString(idValue, CultureInfo.InvariantCulture));
					SqliteCommand update = connection.CreateCommand();
					update.Transaction = (SqliteTransaction)transaction;
					((DbCommand)(object)update).CommandText = "UPDATE datev_transfer_requests SET status='transferring', attempts=attempts+1, last_error=NULL WHERE id=$id AND status='queued'";
					update.Parameters.AddWithValue("$id", (object)id.ToString());
					if (await ((DbCommand)(object)update).ExecuteNonQueryAsync(cancellationToken) != 1)
					{
						await transaction.RollbackAsync(cancellationToken);
						datevTransferRequest = null;
					}
					else
					{
						await transaction.CommitAsync(cancellationToken);
						datevTransferRequest = await GetByIdAsync(id, cancellationToken);
					}
				}
			}
			result = datevTransferRequest;
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

	public async Task<DatevTransferRequest?> MarkDeliveredAsync(Guid requestId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await MarkFinalizedAsync(requestId, cancellationToken);
	}

	public Task<DatevTransferRequest?> MarkBridgeStagedAsync(Guid requestId, DateTimeOffset stagedAt, CancellationToken cancellationToken = default)
	{
		return UpdateTimedStateAsync(requestId, "bridge-staged", "bridge_staged_at", stagedAt, cancellationToken, "transferring");
	}

	public Task<DatevTransferRequest?> MarkWatchfolderDeliveredAsync(Guid requestId, DateTimeOffset deliveredAt, CancellationToken cancellationToken = default(CancellationToken))
	{
		return UpdateTimedStateAsync(requestId, "watchfolder-delivered", "watchfolder_delivered_at", deliveredAt, cancellationToken, "bridge-staged", "watchfolder-delivered");
	}

	public Task<DatevTransferRequest?> MarkWatchfolderDeliveredAsync(Guid requestId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return MarkWatchfolderDeliveredAsync(requestId, DateTimeOffset.UtcNow, cancellationToken);
	}

	public async Task UpdateBridgeAttemptsAsync(Guid requestId, int attempts, CancellationToken cancellationToken = default)
	{
		if (attempts < 1 || attempts > 1000) throw new ArgumentOutOfRangeException(nameof(attempts));
		await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
		var command = connection.CreateCommand();
		command.CommandText = "UPDATE datev_transfer_requests SET attempts=MAX(attempts,$attempts) WHERE id=$id";
		command.Parameters.AddWithValue("$attempts", attempts); command.Parameters.AddWithValue("$id", requestId.ToString());
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public Task<DatevTransferRequest?> MarkAwaitingDatevConfirmationAsync(Guid requestId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return UpdateStateAsync(requestId, "awaiting-datev-confirmation", "Die Datei liegt im DATEV-Watchfolder; BTTnext hat den Abschluss noch nicht bestätigt.", cancellationToken, "watchfolder-delivered");
	}

	public Task<DatevTransferRequest?> MarkFinalizedAsync(Guid requestId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return UpdateStateAsync(requestId, "finalized", null, cancellationToken, "watchfolder-delivered", "awaiting-datev-confirmation");
	}

	public async Task<DatevTransferRequest?> MarkFailedAsync(Guid requestId, string error, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(error))
		{
			throw new ArgumentException("Eine Fehlerbeschreibung ist erforderlich.", "error");
		}
		string safe = error.Trim();
		if (safe.Length > 1000)
		{
			safe = safe.Substring(0, 1000);
		}
		return await UpdateStateAsync(requestId, "failed", safe, cancellationToken);
	}

	public async Task<DatevTransferRequest?> RetryAsync(Guid documentId, string requestedBy, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (documentId == Guid.Empty)
		{
			throw new ArgumentException("Eine Dokument-ID ist erforderlich.", "documentId");
		}
		if (string.IsNullOrWhiteSpace(requestedBy))
		{
			throw new ArgumentException("Ein Benutzer ist erforderlich.", "requestedBy");
		}
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DatevTransferRequest result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "UPDATE datev_transfer_requests SET status='queued', attempts=0, requested_at=$at, requested_by=$by, last_error=NULL, bridge_staged_at=NULL, watchfolder_delivered_at=NULL WHERE document_id=$document AND status='failed'";
			command.Parameters.AddWithValue("$document", (object)documentId.ToString());
			command.Parameters.AddWithValue("$at", (object)DateTimeOffset.UtcNow.ToString("O"));
			command.Parameters.AddWithValue("$by", (object)requestedBy.Trim());
			result = ((await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken) == 1) ? (await GetByDocumentAsync(documentId, cancellationToken)) : null);
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

	public async Task<int> RecoverInProgressAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		SqliteConnection connection = new SqliteConnection(_connectionString);
		int result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "UPDATE datev_transfer_requests SET status='failed', last_error='Transferdienst wurde während der Übergabe neu gestartet.' WHERE status='transferring'";
			result = await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
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

	private static DatevTransferRequest Read(SqliteDataReader reader)
	{
		return new DatevTransferRequest(Guid.Parse(((DbDataReader)(object)reader).GetString(0)), Guid.Parse(((DbDataReader)(object)reader).GetString(1)), ((DbDataReader)(object)reader).GetString(2), ((DbDataReader)(object)reader).GetString(3), DateTimeOffset.Parse(((DbDataReader)(object)reader).GetString(4)), ((DbDataReader)(object)reader).GetString(5), ((DbDataReader)(object)reader).GetInt32(6), ((DbDataReader)(object)reader).IsDBNull(7) ? null : ((DbDataReader)(object)reader).GetString(7), ReadDate(reader, 8), ReadDate(reader, 9));
	}

	private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
	{
		return ((DbDataReader)(object)reader).IsDBNull(ordinal) ? null : DateTimeOffset.Parse(((DbDataReader)(object)reader).GetString(ordinal), CultureInfo.InvariantCulture);
	}

	private async Task<DatevTransferRequest?> UpdateTimedStateAsync(Guid requestId, string status, string timestampColumn, DateTimeOffset timestamp, CancellationToken cancellationToken, params string[] expectedStatuses)
	{
		if (requestId == Guid.Empty) throw new ArgumentException("Eine Transferauftrags-ID ist erforderlich.", nameof(requestId));
		if (timestampColumn is not ("bridge_staged_at" or "watchfolder_delivered_at")) throw new ArgumentOutOfRangeException(nameof(timestampColumn));
		await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
		var command = connection.CreateCommand();
		var placeholders = expectedStatuses.Select((_, index) => "$expected" + index).ToArray();
		command.CommandText = $"UPDATE datev_transfer_requests SET status=$status,{timestampColumn}=COALESCE({timestampColumn},$at),last_error=NULL WHERE id=$id AND status IN ({string.Join(",", placeholders)})";
		command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$at", timestamp.ToString("O")); command.Parameters.AddWithValue("$id", requestId.ToString());
		for (var index = 0; index < expectedStatuses.Length; index++) command.Parameters.AddWithValue(placeholders[index], expectedStatuses[index]);
		return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? await GetByIdAsync(requestId, cancellationToken) : null;
	}

	private async Task<DatevTransferRequest?> UpdateStateAsync(Guid requestId, string status, string? error, CancellationToken cancellationToken, params string[] expectedStatuses)
	{
		if (requestId == Guid.Empty)
		{
			throw new ArgumentException("Eine Transferauftrags-ID ist erforderlich.", "requestId");
		}
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DatevTransferRequest result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			string[] statuses = ((expectedStatuses != null && expectedStatuses.Length > 0) ? expectedStatuses : new string[1] { "transferring" });
			string[] placeholders = statuses.Select((string _, int num) => "$expected" + num).ToArray();
			((DbCommand)(object)command).CommandText = "UPDATE datev_transfer_requests SET status=$status,last_error=$error WHERE id=$id AND status IN (" + string.Join(",", placeholders) + ")";
			command.Parameters.AddWithValue("$status", (object)status);
			command.Parameters.AddWithValue("$error", ((object)error) ?? ((object)DBNull.Value));
			command.Parameters.AddWithValue("$id", (object)requestId.ToString());
			for (int index = 0; index < statuses.Length; index++)
			{
				command.Parameters.AddWithValue(placeholders[index], (object)statuses[index]);
			}
			result = ((await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken) == 1) ? (await GetByIdAsync(requestId, cancellationToken)) : null);
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

	public async Task<DatevTransferRequest?> GetByIdAsync(Guid requestId, CancellationToken cancellationToken = default)
	{
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DatevTransferRequest result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT id,document_id,package_sha256,status,requested_at,requested_by,attempts,last_error,bridge_staged_at,watchfolder_delivered_at FROM datev_transfer_requests WHERE id=$id";
			command.Parameters.AddWithValue("$id", (object)requestId.ToString());
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			DatevTransferRequest datevTransferRequest;
			try
			{
				datevTransferRequest = ((await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)) ? Read(reader) : null);
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = datevTransferRequest;
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

	private static string NormalizeHash(string value)
	{
		string hash = (value ?? string.Empty).Trim();
		if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
		{
			throw new ArgumentException("Die DATEV-Prüfsumme muss aus 64 Hexadezimalzeichen bestehen.", "value");
		}
		return hash.ToUpperInvariant();
	}

	private static async Task EnsureColumnAsync(SqliteConnection connection, string column, string definition, CancellationToken cancellationToken)
	{
		var check = connection.CreateCommand(); check.CommandText = "PRAGMA table_info(datev_transfer_requests)";
		await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
		{
			while (await reader.ReadAsync(cancellationToken))
				if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
		}
		var alter = connection.CreateCommand(); alter.CommandText = $"ALTER TABLE datev_transfer_requests ADD COLUMN {column} {definition}";
		await alter.ExecuteNonQueryAsync(cancellationToken);
	}
}
