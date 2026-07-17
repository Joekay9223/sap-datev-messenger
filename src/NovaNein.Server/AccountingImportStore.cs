using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class AccountingImportStore
{
	private readonly string _connectionString;

	private readonly string _root;

	private readonly DatevBookingCsvParser _parser;

	public AccountingImportStore(IConfiguration configuration, DatevBookingCsvParser parser)
	{
		_parser = parser;
		_connectionString = "Data Source=" + (configuration["Storage:DatabasePath"] ?? "data/novanein.db") + ";Default Timeout=5";
		_root = Path.GetFullPath(configuration["Storage:AccountingImportRoot"] ?? "data/accounting-imports");
	}

	public async Task InitializeAsync(CancellationToken ct = default(CancellationToken))
	{
		Directory.CreateDirectory(_root);
		SqliteConnection connection = await OpenAsync(ct);
		try
		{
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;\nCREATE TABLE IF NOT EXISTS accounting_import_batches (\n  id TEXT PRIMARY KEY, original_file_name TEXT NOT NULL, original_path TEXT NOT NULL,\n  file_sha256 TEXT NOT NULL UNIQUE, status INTEGER NOT NULL, period_start TEXT NULL, period_end TEXT NULL,\n  row_count INTEGER NOT NULL, warning_count INTEGER NOT NULL, error_count INTEGER NOT NULL,\n  parser_version TEXT NOT NULL, imported_at TEXT NOT NULL, imported_by TEXT NOT NULL,\n  confirmed_at TEXT NULL, confirmed_by TEXT NULL\n);\nCREATE TABLE IF NOT EXISTS accounting_import_issues (\n  id INTEGER PRIMARY KEY AUTOINCREMENT, batch_id TEXT NOT NULL, severity INTEGER NOT NULL,\n  row_number INTEGER NULL, message TEXT NOT NULL, FOREIGN KEY(batch_id) REFERENCES accounting_import_batches(id)\n);\nCREATE TABLE IF NOT EXISTS datev_booking_rows (\n  id TEXT PRIMARY KEY, batch_id TEXT NOT NULL, row_number INTEGER NOT NULL, document_date TEXT NOT NULL,\n  amount TEXT NOT NULL, debit_credit TEXT NOT NULL, currency TEXT NOT NULL, account TEXT NOT NULL,\n  counter_account TEXT NOT NULL, bu_code TEXT NOT NULL, reference1 TEXT NOT NULL, reference2 TEXT NOT NULL,\n  booking_text TEXT NOT NULL, normalized_reference TEXT NOT NULL, partner_account TEXT NOT NULL,\n  row_sha256 TEXT NOT NULL, raw_json TEXT NOT NULL, FOREIGN KEY(batch_id) REFERENCES accounting_import_batches(id),\n  UNIQUE(batch_id,row_number)\n);\nCREATE INDEX IF NOT EXISTS ix_datev_rows_batch_reference ON datev_booking_rows(batch_id, normalized_reference);\nCREATE TABLE IF NOT EXISTS reconciliation_decisions (\n  id INTEGER PRIMARY KEY AUTOINCREMENT, reconciliation_id TEXT NOT NULL, batch_id TEXT NOT NULL,\n  expected_hash TEXT NOT NULL, decision TEXT NOT NULL, datev_row_id TEXT NULL, reason TEXT NOT NULL,\n  decided_at TEXT NOT NULL, decided_by TEXT NOT NULL\n);\nCREATE INDEX IF NOT EXISTS ix_reconciliation_decisions_item ON reconciliation_decisions(reconciliation_id, decided_at DESC);";
			await ((DbCommand)(object)command).ExecuteNonQueryAsync(ct);
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
	}

	public async Task<AccountingImportBatch> ImportAsync(string fileName, byte[] content, string actor, CancellationToken ct = default(CancellationToken))
	{
		string safeName = Path.GetFileName(fileName);
		if (string.IsNullOrWhiteSpace(safeName) || safeName != fileName)
		{
			throw new InvalidDataException("Der DATEV-Dateiname ist ungültig.");
		}
		string sha = Convert.ToHexString(SHA256.HashData(content));
		SqliteConnection connection = await OpenAsync(ct);
		AccountingImportBatch result;
		try
		{
			SqliteCommand duplicate = connection.CreateCommand();
			((DbCommand)(object)duplicate).CommandText = "SELECT id FROM accounting_import_batches WHERE file_sha256=$sha";
			duplicate.Parameters.AddWithValue("$sha", (object)sha);
			if (await ((DbCommand)(object)duplicate).ExecuteScalarAsync(ct) != null)
			{
				throw new InvalidOperationException("Diese DATEV-Datei wurde bereits importiert.");
			}
			DatevBookingParseResult parsed = _parser.Parse(content);
			Guid id = Guid.NewGuid();
			DateTimeOffset now = DateTimeOffset.UtcNow;
			string path = Path.Combine(_root, $"{id:N}-{sha.Substring(0, 12)}.csv");
			string temporary = path + ".tmp";
			await File.WriteAllBytesAsync(temporary, content, ct);
			File.Move(temporary, path);
			AccountingImportBatch accountingImportBatch;
			await using (DbTransaction transaction = await ((DbConnection)(object)connection).BeginTransactionAsync(ct))
			{
				try
				{
					SqliteCommand insert = connection.CreateCommand();
					insert.Transaction = (SqliteTransaction)transaction;
					((DbCommand)(object)insert).CommandText = "INSERT INTO accounting_import_batches VALUES($id,$name,$path,$sha,0,$from,$to,$rows,$warnings,$errors,$parser,$at,$by,NULL,NULL)";
					insert.Parameters.AddWithValue("$id", (object)id.ToString());
					insert.Parameters.AddWithValue("$name", (object)safeName);
					insert.Parameters.AddWithValue("$path", (object)path);
					insert.Parameters.AddWithValue("$sha", (object)sha);
					insert.Parameters.AddWithValue("$from", ((object)parsed.PeriodStart?.ToString("yyyy-MM-dd")) ?? ((object)DBNull.Value));
					insert.Parameters.AddWithValue("$to", ((object)parsed.PeriodEnd?.ToString("yyyy-MM-dd")) ?? ((object)DBNull.Value));
					insert.Parameters.AddWithValue("$rows", (object)parsed.Rows.Count);
					insert.Parameters.AddWithValue("$warnings", (object)parsed.Issues.Count((AccountingImportIssue x) => x.Severity == AccountingIssueSeverity.Warning));
					insert.Parameters.AddWithValue("$errors", (object)parsed.Issues.Count((AccountingImportIssue x) => x.Severity == AccountingIssueSeverity.Error));
					insert.Parameters.AddWithValue("$parser", (object)"datev-bookings-v1");
					insert.Parameters.AddWithValue("$at", (object)now.ToString("O"));
					insert.Parameters.AddWithValue("$by", (object)actor);
					await ((DbCommand)(object)insert).ExecuteNonQueryAsync(ct);
					foreach (AccountingImportIssue issue in parsed.Issues)
					{
						SqliteCommand c = connection.CreateCommand();
						c.Transaction = (SqliteTransaction)transaction;
						((DbCommand)(object)c).CommandText = "INSERT INTO accounting_import_issues(batch_id,severity,row_number,message) VALUES($b,$s,$r,$m)";
						c.Parameters.AddWithValue("$b", (object)id.ToString());
						c.Parameters.AddWithValue("$s", (object)(int)issue.Severity);
						c.Parameters.AddWithValue("$r", ((object)issue.RowNumber) ?? DBNull.Value);
						c.Parameters.AddWithValue("$m", (object)issue.Message);
						await ((DbCommand)(object)c).ExecuteNonQueryAsync(ct);
					}
					foreach (DatevBookingRowDraft row in parsed.Rows)
					{
						SqliteCommand c2 = connection.CreateCommand();
						c2.Transaction = (SqliteTransaction)transaction;
						((DbCommand)(object)c2).CommandText = "INSERT INTO datev_booking_rows VALUES($id,$b,$n,$d,$a,$dc,$cur,$k,$g,$bu,$r1,$r2,$t,$nr,$p,$sha,$raw)";
						c2.Parameters.AddWithValue("$id", (object)Guid.NewGuid().ToString());
						c2.Parameters.AddWithValue("$b", (object)id.ToString());
						c2.Parameters.AddWithValue("$n", (object)row.RowNumber);
						c2.Parameters.AddWithValue("$d", (object)row.DocumentDate.ToString("yyyy-MM-dd"));
						c2.Parameters.AddWithValue("$a", (object)row.Amount.ToString(CultureInfo.InvariantCulture));
						c2.Parameters.AddWithValue("$dc", (object)row.DebitCredit);
						c2.Parameters.AddWithValue("$cur", (object)row.Currency);
						c2.Parameters.AddWithValue("$k", (object)row.Account);
						c2.Parameters.AddWithValue("$g", (object)row.CounterAccount);
						c2.Parameters.AddWithValue("$bu", (object)row.BuCode);
						c2.Parameters.AddWithValue("$r1", (object)row.Reference1);
						c2.Parameters.AddWithValue("$r2", (object)row.Reference2);
						c2.Parameters.AddWithValue("$t", (object)row.BookingText);
						c2.Parameters.AddWithValue("$nr", (object)row.NormalizedReference);
						c2.Parameters.AddWithValue("$p", (object)row.PartnerAccount);
						c2.Parameters.AddWithValue("$sha", (object)row.RowSha256);
						c2.Parameters.AddWithValue("$raw", (object)row.RawJson);
						await ((DbCommand)(object)c2).ExecuteNonQueryAsync(ct);
					}
					await transaction.CommitAsync(ct);
				}
				catch
				{
					await transaction.RollbackAsync(ct);
					File.Delete(path);
					throw;
				}
				accountingImportBatch = await GetAsync(id, includeRows: true, ct);
			}
			result = accountingImportBatch;
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

	public async Task<IReadOnlyList<AccountingImportBatch>> ListAsync(CancellationToken ct = default(CancellationToken))
	{
		SqliteConnection c = await OpenAsync(ct);
		IReadOnlyList<AccountingImportBatch> result2;
		try
		{
			SqliteCommand q = c.CreateCommand();
			((DbCommand)(object)q).CommandText = "SELECT * FROM accounting_import_batches ORDER BY imported_at DESC";
			List<AccountingImportBatch> result = new List<AccountingImportBatch>();
			SqliteDataReader r = await q.ExecuteReaderAsync(ct);
			IReadOnlyList<AccountingImportBatch> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)r).ReadAsync(ct))
				{
					result.Add(ReadBatch(r));
				}
				readOnlyList = result;
			}
			finally
			{
				if (r != null)
				{
					await ((DbDataReader)(object)r).DisposeAsync();
				}
			}
			result2 = readOnlyList;
		}
		finally
		{
			if (c != null)
			{
				await ((DbConnection)(object)c).DisposeAsync();
			}
		}
		return result2;
	}

	public async Task<AccountingImportBatch?> GetAsync(Guid id, bool includeRows = false, CancellationToken ct = default(CancellationToken))
	{
		SqliteConnection c = await OpenAsync(ct);
		AccountingImportBatch result;
		try
		{
			SqliteCommand q = c.CreateCommand();
			((DbCommand)(object)q).CommandText = "SELECT * FROM accounting_import_batches WHERE id=$id";
			q.Parameters.AddWithValue("$id", (object)id.ToString());
			SqliteDataReader r = await q.ExecuteReaderAsync(ct);
			AccountingImportBatch accountingImportBatch;
			try
			{
				if (!(await ((DbDataReader)(object)r).ReadAsync(ct)))
				{
					accountingImportBatch = null;
				}
				else
				{
					AccountingImportBatch batch = ReadBatch(r);
					await ((DbDataReader)(object)r).DisposeAsync();
					List<AccountingImportIssue> issues = new List<AccountingImportIssue>();
					q = c.CreateCommand();
					((DbCommand)(object)q).CommandText = "SELECT severity,row_number,message FROM accounting_import_issues WHERE batch_id=$id ORDER BY id";
					q.Parameters.AddWithValue("$id", (object)id.ToString());
					SqliteDataReader ir = await q.ExecuteReaderAsync(ct);
					try
					{
						while (await ((DbDataReader)(object)ir).ReadAsync(ct))
						{
							issues.Add(new AccountingImportIssue((AccountingIssueSeverity)((DbDataReader)(object)ir).GetInt32(0), ((DbDataReader)(object)ir).IsDBNull(1) ? ((int?)null) : new int?(((DbDataReader)(object)ir).GetInt32(1)), ((DbDataReader)(object)ir).GetString(2)));
						}
					}
					finally
					{
						if (ir != null)
						{
							await ((DbDataReader)(object)ir).DisposeAsync();
						}
					}
					IReadOnlyList<DatevBookingRow> readOnlyList = ((!includeRows) ? null : (await ReadRowsAsync(c, id, ct)));
					IReadOnlyList<DatevBookingRow> rows = readOnlyList;
					accountingImportBatch = batch with
					{
						Issues = issues,
						Rows = rows
					};
				}
			}
			finally
			{
				if (r != null)
				{
					await ((DbDataReader)(object)r).DisposeAsync();
				}
			}
			result = accountingImportBatch;
		}
		finally
		{
			if (c != null)
			{
				await ((DbConnection)(object)c).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<AccountingImportBatch?> GetActiveAsync(CancellationToken ct = default(CancellationToken))
	{
		SqliteConnection c = await OpenAsync(ct);
		AccountingImportBatch result;
		try
		{
			SqliteCommand q = c.CreateCommand();
			((DbCommand)(object)q).CommandText = "SELECT id FROM accounting_import_batches WHERE status=1 ORDER BY confirmed_at DESC LIMIT 1";
			object value = await ((DbCommand)(object)q).ExecuteScalarAsync(ct);
			AccountingImportBatch accountingImportBatch = ((value != null) ? (await GetAsync(Guid.Parse((string)value), includeRows: true, ct)) : null);
			result = accountingImportBatch;
		}
		finally
		{
			if (c != null)
			{
				await ((DbConnection)(object)c).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<AccountingImportBatch> ConfirmAsync(Guid id, string actor, CancellationToken ct = default(CancellationToken))
	{
		SqliteConnection c = await OpenAsync(ct);
		AccountingImportBatch result;
		try
		{
			SqliteCommand q = c.CreateCommand();
			((DbCommand)(object)q).CommandText = "UPDATE accounting_import_batches SET status=1,confirmed_at=$at,confirmed_by=$by WHERE id=$id AND status=0 AND error_count=0";
			q.Parameters.AddWithValue("$id", (object)id.ToString());
			q.Parameters.AddWithValue("$at", (object)DateTimeOffset.UtcNow.ToString("O"));
			q.Parameters.AddWithValue("$by", (object)actor);
			if (await ((DbCommand)(object)q).ExecuteNonQueryAsync(ct) != 1)
			{
				throw new InvalidOperationException("Der Import ist unbekannt, bereits bestätigt oder enthält blockierende Fehler.");
			}
			result = await GetAsync(id, includeRows: true, ct);
		}
		finally
		{
			if (c != null)
			{
				await ((DbConnection)(object)c).DisposeAsync();
			}
		}
		return result;
	}

	public async Task SaveDecisionAsync(string reconciliationId, Guid batchId, ReconciliationDecisionRequest request, string actor, CancellationToken ct = default(CancellationToken))
	{
		if (request.Reason.Trim().Length < 5)
		{
			throw new ArgumentException("Bitte eine nachvollziehbare Begründung mit mindestens fünf Zeichen angeben.");
		}
		SqliteConnection c = await OpenAsync(ct);
		try
		{
			SqliteCommand q = c.CreateCommand();
			((DbCommand)(object)q).CommandText = "INSERT INTO reconciliation_decisions(reconciliation_id,batch_id,expected_hash,decision,datev_row_id,reason,decided_at,decided_by) VALUES($id,$b,$h,$d,$r,$reason,$at,$by)";
			q.Parameters.AddWithValue("$id", (object)reconciliationId);
			q.Parameters.AddWithValue("$b", (object)batchId.ToString());
			q.Parameters.AddWithValue("$h", (object)request.ExpectedHash);
			q.Parameters.AddWithValue("$d", (object)request.Decision.Trim());
			q.Parameters.AddWithValue("$r", ((object)request.DatevRowId?.ToString()) ?? ((object)DBNull.Value));
			q.Parameters.AddWithValue("$reason", (object)request.Reason.Trim());
			q.Parameters.AddWithValue("$at", (object)DateTimeOffset.UtcNow.ToString("O"));
			q.Parameters.AddWithValue("$by", (object)actor);
			await ((DbCommand)(object)q).ExecuteNonQueryAsync(ct);
		}
		finally
		{
			if (c != null)
			{
				await ((DbConnection)(object)c).DisposeAsync();
			}
		}
	}

	public async Task<(string ExpectedHash, string Reason, string Actor, DateTimeOffset At)?> LatestDecisionAsync(string id, Guid batchId, CancellationToken ct = default(CancellationToken))
	{
		SqliteConnection c = await OpenAsync(ct);
		(string ExpectedHash, string Reason, string Actor, DateTimeOffset At)? result;
		try
		{
			SqliteCommand q = c.CreateCommand();
			((DbCommand)(object)q).CommandText = "SELECT expected_hash,reason,decided_by,decided_at FROM reconciliation_decisions WHERE reconciliation_id=$id AND batch_id=$b ORDER BY id DESC LIMIT 1";
			q.Parameters.AddWithValue("$id", (object)id);
			q.Parameters.AddWithValue("$b", (object)batchId.ToString());
			SqliteDataReader r = await q.ExecuteReaderAsync(ct);
			(string ExpectedHash, string Reason, string Actor, DateTimeOffset At)? tuple;
			try
			{
				tuple = ((await ((DbDataReader)(object)r).ReadAsync(ct)) ? new(string, string, string, DateTimeOffset)?((((DbDataReader)(object)r).GetString(0), ((DbDataReader)(object)r).GetString(1), ((DbDataReader)(object)r).GetString(2), DateTimeOffset.Parse(((DbDataReader)(object)r).GetString(3)))) : (((string, string, string, DateTimeOffset)?)null));
			}
			finally
			{
				if (r != null)
				{
					await ((DbDataReader)(object)r).DisposeAsync();
				}
			}
			result = tuple;
		}
		finally
		{
			if (c != null)
			{
				await ((DbConnection)(object)c).DisposeAsync();
			}
		}
		return result;
	}

	private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
	{
		SqliteConnection c = new SqliteConnection(_connectionString);
		await ((DbConnection)(object)c).OpenAsync(ct);
		SqliteCommand q = c.CreateCommand();
		((DbCommand)(object)q).CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON";
		await ((DbCommand)(object)q).ExecuteNonQueryAsync(ct);
		return c;
	}

	private static AccountingImportBatch ReadBatch(SqliteDataReader r)
	{
		return new AccountingImportBatch(Guid.Parse(((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("id"))), ((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("original_file_name")), ((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("file_sha256")), (AccountingImportStatus)((DbDataReader)(object)r).GetInt32(((DbDataReader)(object)r).GetOrdinal("status")), ((DbDataReader)(object)r).IsDBNull(((DbDataReader)(object)r).GetOrdinal("period_start")) ? ((DateOnly?)null) : new DateOnly?(DateOnly.Parse(((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("period_start")))), ((DbDataReader)(object)r).IsDBNull(((DbDataReader)(object)r).GetOrdinal("period_end")) ? ((DateOnly?)null) : new DateOnly?(DateOnly.Parse(((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("period_end")))), ((DbDataReader)(object)r).GetInt32(((DbDataReader)(object)r).GetOrdinal("row_count")), ((DbDataReader)(object)r).GetInt32(((DbDataReader)(object)r).GetOrdinal("warning_count")), ((DbDataReader)(object)r).GetInt32(((DbDataReader)(object)r).GetOrdinal("error_count")), ((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("parser_version")), DateTimeOffset.Parse(((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("imported_at"))), ((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("imported_by")), ((DbDataReader)(object)r).IsDBNull(((DbDataReader)(object)r).GetOrdinal("confirmed_at")) ? ((DateTimeOffset?)null) : new DateTimeOffset?(DateTimeOffset.Parse(((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("confirmed_at")))), ((DbDataReader)(object)r).IsDBNull(((DbDataReader)(object)r).GetOrdinal("confirmed_by")) ? null : ((DbDataReader)(object)r).GetString(((DbDataReader)(object)r).GetOrdinal("confirmed_by")));
	}

	private static async Task<IReadOnlyList<DatevBookingRow>> ReadRowsAsync(SqliteConnection c, Guid id, CancellationToken ct)
	{
		SqliteCommand q = c.CreateCommand();
		((DbCommand)(object)q).CommandText = "SELECT * FROM datev_booking_rows WHERE batch_id=$id ORDER BY row_number";
		q.Parameters.AddWithValue("$id", (object)id.ToString());
		List<DatevBookingRow> list = new List<DatevBookingRow>();
		SqliteDataReader r = await q.ExecuteReaderAsync(ct);
		IReadOnlyList<DatevBookingRow> result;
		try
		{
			while (await ((DbDataReader)(object)r).ReadAsync(ct))
			{
				list.Add(new DatevBookingRow(Guid.Parse(((DbDataReader)(object)r).GetString(0)), Guid.Parse(((DbDataReader)(object)r).GetString(1)), ((DbDataReader)(object)r).GetInt32(2), DateOnly.Parse(((DbDataReader)(object)r).GetString(3)), decimal.Parse(((DbDataReader)(object)r).GetString(4), CultureInfo.InvariantCulture), ((DbDataReader)(object)r).GetString(5), ((DbDataReader)(object)r).GetString(6), ((DbDataReader)(object)r).GetString(7), ((DbDataReader)(object)r).GetString(8), ((DbDataReader)(object)r).GetString(9), ((DbDataReader)(object)r).GetString(10), ((DbDataReader)(object)r).GetString(11), ((DbDataReader)(object)r).GetString(12), ((DbDataReader)(object)r).GetString(13), ((DbDataReader)(object)r).GetString(14), ((DbDataReader)(object)r).GetString(15), ((DbDataReader)(object)r).GetString(16)));
			}
			result = list;
		}
		finally
		{
			if (r != null)
			{
				await ((DbDataReader)(object)r).DisposeAsync();
			}
		}
		return result;
	}
}
