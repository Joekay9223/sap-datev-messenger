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

public sealed class PdfInboxStore
{
	private readonly string _connectionString;

	public PdfInboxStore(IConfiguration configuration)
	{
		_connectionString = "Data Source=" + (configuration["Storage:DatabasePath"] ?? "data/novanein.db");
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		string dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
		string directory = Path.GetDirectoryName(dataSource);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}
		SqliteConnection connection = new SqliteConnection(_connectionString);
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "CREATE TABLE IF NOT EXISTS pdf_inbox (\n  id TEXT PRIMARY KEY,\n  sha256 TEXT NOT NULL UNIQUE,\n  original_file_name TEXT NOT NULL,\n  status INTEGER NOT NULL,\n  invoice_number TEXT NULL,\n  business_partner TEXT NULL,\n  gross_amount TEXT NULL,\n  currency TEXT NULL,\n  invoice_date TEXT NULL,\n  assigned_direction INTEGER NULL,\n  assigned_doc_entry INTEGER NULL,\n  assigned_document_id TEXT NULL,\n  created_at TEXT NOT NULL,\n  created_by TEXT NULL,\n  updated_at TEXT NOT NULL,\n  assignment_actor TEXT NULL,\n  assigned_at TEXT NULL,\n  last_error TEXT NULL\n);";
			await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "invoice_number", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "business_partner", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "gross_amount", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "currency", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "invoice_date", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "assigned_direction", "INTEGER NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "assigned_doc_entry", "INTEGER NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "assigned_document_id", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "created_by", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "assignment_actor", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "assigned_at", "TEXT NULL", cancellationToken);
			await EnsureColumnAsync(connection, "pdf_inbox", "last_error", "TEXT NULL", cancellationToken);
			SqliteCommand indexes = connection.CreateCommand();
			((DbCommand)(object)indexes).CommandText = "CREATE INDEX IF NOT EXISTS ix_pdf_inbox_status_created ON pdf_inbox(status, created_at);\nCREATE INDEX IF NOT EXISTS ix_pdf_inbox_assigned_sap ON pdf_inbox(assigned_direction, assigned_doc_entry);";
			await ((DbCommand)(object)indexes).ExecuteNonQueryAsync(cancellationToken);
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
	}

	public async Task<PdfInboxItem> CreateAsync(string sha256, string originalFileName, ExtractedInvoiceFacts? facts = null, CancellationToken cancellationToken = default(CancellationToken), string? actor = null)
	{
		string hash = NormalizeHash(sha256);
		if (string.IsNullOrWhiteSpace(originalFileName))
		{
			throw new ArgumentException("Der PDF-Dateiname ist erforderlich.", "originalFileName");
		}
		string fileName = Path.GetFileName(originalFileName);
		if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("Der PDF-Dateiname ist ungültig.", "originalFileName");
		}
		DateTimeOffset now = DateTimeOffset.UtcNow;
		Guid id = Guid.NewGuid();
		string? invoiceNumber = NullIfEmpty(facts?.InvoiceNumber);
		string? businessPartner = NullIfEmpty(facts?.BusinessPartnerName);
		decimal? grossAmount = (((object)facts != null && facts.GrossAmount > 0m) ? new decimal?(facts.GrossAmount) : ((decimal?)null));
		string? currency = NullIfEmpty(facts?.Currency);
		DateOnly? dateOnly = facts?.InvoiceDate;
		DateOnly? invoiceDate;
		if (dateOnly.HasValue)
		{
			DateOnly date = dateOnly.GetValueOrDefault();
			if (date != DateOnly.MinValue)
			{
				invoiceDate = date;
				goto IL_0164;
			}
		}
		invoiceDate = null;
		goto IL_0164;
		IL_0164:
		PdfInboxItem item = new PdfInboxItem(id, hash, fileName, "unassigned", invoiceNumber, businessPartner, grossAmount, currency, invoiceDate, null, null, null, now, now, null, null, null);
		SqliteConnection connection = new SqliteConnection(_connectionString);
		PdfInboxItem result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "INSERT INTO pdf_inbox(\n  id,sha256,original_file_name,status,invoice_number,business_partner,gross_amount,currency,invoice_date,\n  assigned_direction,assigned_doc_entry,assigned_document_id,created_at,created_by,updated_at,assignment_actor,assigned_at,last_error)\nVALUES($id,$hash,$file,$status,$invoice,$partner,$gross,$currency,$date,NULL,NULL,NULL,$created,$actor,$updated,NULL,NULL,NULL);";
			command.Parameters.AddWithValue("$id", (object)item.Id.ToString());
			command.Parameters.AddWithValue("$hash", (object)item.Sha256);
			command.Parameters.AddWithValue("$file", (object)item.OriginalFileName);
			command.Parameters.AddWithValue("$status", (object)0);
			command.Parameters.AddWithValue("$invoice", ((object)item.InvoiceNumber) ?? ((object)DBNull.Value));
			command.Parameters.AddWithValue("$partner", ((object)item.BusinessPartner) ?? ((object)DBNull.Value));
			SqliteParameterCollection parameters = command.Parameters;
			decimal? grossAmount2 = item.GrossAmount;
			parameters.AddWithValue("$gross", (object)((!grossAmount2.HasValue) ? ((IConvertible)DBNull.Value) : ((IConvertible)grossAmount2.GetValueOrDefault().ToString(CultureInfo.InvariantCulture))));
			command.Parameters.AddWithValue("$currency", ((object)item.Currency) ?? ((object)DBNull.Value));
			command.Parameters.AddWithValue("$date", ((object)item.InvoiceDate?.ToString("yyyy-MM-dd")) ?? ((object)DBNull.Value));
			command.Parameters.AddWithValue("$created", (object)item.CreatedAt.ToString("O"));
			command.Parameters.AddWithValue("$actor", (object)(string.IsNullOrWhiteSpace(actor) ? ((IConvertible)DBNull.Value) : ((IConvertible)actor.Trim())));
			command.Parameters.AddWithValue("$updated", (object)item.UpdatedAt.ToString("O"));
			try
			{
				await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
			}
			catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
			{
				throw new PdfInboxDuplicateException("Diese PDF befindet sich bereits im NovaNein-Eingang.");
			}
			result = item;
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

	public async Task<PdfInboxItem?> GetAsync(Guid id, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (id == Guid.Empty)
		{
			throw new ArgumentException("Eine PDF-Eingangs-ID ist erforderlich.", "id");
		}
		SqliteConnection connection = new SqliteConnection(_connectionString);
		PdfInboxItem result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT id,sha256,original_file_name,status,invoice_number,business_partner,gross_amount,currency,invoice_date,assigned_direction,assigned_doc_entry,assigned_document_id,created_at,created_by,updated_at,assignment_actor,assigned_at,last_error FROM pdf_inbox WHERE id=$id";
			command.Parameters.AddWithValue("$id", (object)id.ToString());
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			PdfInboxItem pdfInboxItem;
			try
			{
				pdfInboxItem = ((await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)) ? Read(reader) : null);
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = pdfInboxItem;
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

	public async Task<IReadOnlyList<PdfInboxItem>> ListAsync(PdfInboxStatus? status = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		List<PdfInboxItem> result = new List<PdfInboxItem>();
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlyList<PdfInboxItem> result2;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT id,sha256,original_file_name,status,invoice_number,business_partner,gross_amount,currency,invoice_date,assigned_direction,assigned_doc_entry,assigned_document_id,created_at,created_by,updated_at,assignment_actor,assigned_at,last_error FROM pdf_inbox WHERE ($status IS NULL OR status=$status) ORDER BY created_at DESC";
			command.Parameters.AddWithValue("$status", (!status.HasValue) ? DBNull.Value : ((object)(int)status.Value));
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<PdfInboxItem> readOnlyList;
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

	public async Task<PdfInboxItem?> AssignAsync(Guid id, SapDocumentIdentity sap, Guid documentId, string actor, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (id == Guid.Empty)
		{
			throw new ArgumentException("Eine PDF-Eingangs-ID ist erforderlich.", "id");
		}
		if (documentId == Guid.Empty)
		{
			throw new ArgumentException("Die NovaNein-Dokument-ID ist erforderlich.", "documentId");
		}
		if (sap.DocEntry <= 0)
		{
			throw new ArgumentOutOfRangeException("sap");
		}
		if (string.IsNullOrWhiteSpace(actor))
		{
			throw new ArgumentException("Ein Akteur ist erforderlich.", "actor");
		}
		DateTimeOffset now = DateTimeOffset.UtcNow;
		SqliteConnection connection = new SqliteConnection(_connectionString);
		PdfInboxItem result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "UPDATE pdf_inbox\n   SET status=$assigned, assigned_direction=$direction, assigned_doc_entry=$entry,\n       assigned_document_id=$document, assignment_actor=$actor, assigned_at=$at,\n       updated_at=$at, last_error=NULL\n WHERE id=$id AND status=$unassigned;";
			command.Parameters.AddWithValue("$assigned", (object)1);
			command.Parameters.AddWithValue("$direction", (object)(int)sap.Direction);
			command.Parameters.AddWithValue("$entry", (object)sap.DocEntry);
			command.Parameters.AddWithValue("$document", (object)documentId.ToString());
			command.Parameters.AddWithValue("$actor", (object)actor.Trim());
			command.Parameters.AddWithValue("$at", (object)now.ToString("O"));
			command.Parameters.AddWithValue("$id", (object)id.ToString());
			command.Parameters.AddWithValue("$unassigned", (object)0);
			if (await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken) != 1)
			{
				if ((object)(await GetAsync(id, cancellationToken)) != null)
				{
					throw new PdfInboxAlreadyAssignedException("Die PDF wurde bereits von einem anderen Benutzer zugeordnet.");
				}
				result = null;
			}
			else
			{
				result = await GetAsync(id, cancellationToken);
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

	public async Task<bool> RejectAsync(Guid id, string reason, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (id == Guid.Empty)
		{
			throw new ArgumentException("Eine PDF-Eingangs-ID ist erforderlich.", "id");
		}
		if (string.IsNullOrWhiteSpace(reason))
		{
			throw new ArgumentException("Ein Ablehnungsgrund ist erforderlich.", "reason");
		}
		string commandText = "UPDATE pdf_inbox SET status=$rejected,last_error=$reason,updated_at=$at WHERE id=$id AND status=$unassigned";
		SqliteConnection connection = new SqliteConnection(_connectionString);
		bool result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = commandText;
			command.Parameters.AddWithValue("$rejected", (object)2);
			command.Parameters.AddWithValue("$reason", (object)((reason.Trim().Length > 500) ? reason.Trim().Substring(0, 500) : reason.Trim()));
			command.Parameters.AddWithValue("$at", (object)DateTimeOffset.UtcNow.ToString("O"));
			command.Parameters.AddWithValue("$id", (object)id.ToString());
			command.Parameters.AddWithValue("$unassigned", (object)0);
			result = await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken) == 1;
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
		HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlySet<string> result2;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT sha256 FROM pdf_inbox WHERE status <> $rejected";
			command.Parameters.AddWithValue("$rejected", (object)2);
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlySet<string> readOnlySet;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					result.Add(((DbDataReader)(object)reader).GetString(0));
				}
				readOnlySet = result;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result2 = readOnlySet;
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

	private static PdfInboxItem Read(SqliteDataReader reader)
	{
		return new PdfInboxItem(Guid.Parse(((DbDataReader)(object)reader).GetString(0)), ((DbDataReader)(object)reader).GetString(1), ((DbDataReader)(object)reader).GetString(2), ((PdfInboxStatus)((DbDataReader)(object)reader).GetInt32(3)).ToWireValue(), ReadString(reader, 4), ReadString(reader, 5), ReadDecimal(reader, 6), ReadString(reader, 7), ReadDateOnly(reader, 8), ((DbDataReader)(object)reader).IsDBNull(9) ? ((DocumentDirection?)null) : new DocumentDirection?((DocumentDirection)((DbDataReader)(object)reader).GetInt32(9)), ((DbDataReader)(object)reader).IsDBNull(10) ? ((int?)null) : new int?(((DbDataReader)(object)reader).GetInt32(10)), ((DbDataReader)(object)reader).IsDBNull(11) ? ((Guid?)null) : new Guid?(Guid.Parse(((DbDataReader)(object)reader).GetString(11))), DateTimeOffset.Parse(((DbDataReader)(object)reader).GetString(12)), DateTimeOffset.Parse(((DbDataReader)(object)reader).GetString(14)), ReadString(reader, 15), ReadDateTime(reader, 16), ReadString(reader, 17));
	}

	private static string? ReadString(SqliteDataReader reader, int ordinal)
	{
		if (!((DbDataReader)(object)reader).IsDBNull(ordinal))
		{
			return ((DbDataReader)(object)reader).GetString(ordinal);
		}
		return null;
	}

	private static decimal? ReadDecimal(SqliteDataReader reader, int ordinal)
	{
		if (!((DbDataReader)(object)reader).IsDBNull(ordinal) && decimal.TryParse(((DbDataReader)(object)reader).GetString(ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
		{
			return value;
		}
		return null;
	}

	private static DateOnly? ReadDateOnly(SqliteDataReader reader, int ordinal)
	{
		if (!((DbDataReader)(object)reader).IsDBNull(ordinal) && DateOnly.TryParse(((DbDataReader)(object)reader).GetString(ordinal), out var value))
		{
			return value;
		}
		return null;
	}

	private static DateTimeOffset? ReadDateTime(SqliteDataReader reader, int ordinal)
	{
		if (!((DbDataReader)(object)reader).IsDBNull(ordinal))
		{
			return DateTimeOffset.Parse(((DbDataReader)(object)reader).GetString(ordinal));
		}
		return null;
	}

	private static string? NullIfEmpty(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim();
		}
		return null;
	}

	private static string NormalizeHash(string value)
	{
		string normalized = (value ?? string.Empty).Trim();
		if (normalized.Length != 64 || !normalized.All(char.IsAsciiHexDigit))
		{
			throw new ArgumentException("Die PDF-SHA-256-Prüfsumme muss 64 Hexadezimalzeichen enthalten.", "value");
		}
		return normalized.ToUpperInvariant();
	}

	private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
	{
		SqliteCommand check = connection.CreateCommand();
		((DbCommand)(object)check).CommandText = "PRAGMA table_info(" + table + ")";
		SqliteDataReader reader = await check.ExecuteReaderAsync(cancellationToken);
		try
		{
			do
			{
				if (!(await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)))
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
					SqliteCommand alter = connection.CreateCommand();
					((DbCommand)(object)alter).CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
					await ((DbCommand)(object)alter).ExecuteNonQueryAsync(cancellationToken);
					break;
				}
			}
			while (!string.Equals(((DbDataReader)(object)reader).GetString(1), column, StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			if (reader != null)
			{
				await ((DbDataReader)(object)reader).DisposeAsync();
			}
		}
	}
}
