using System;
using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class AiReviewAuditStore
{
	private readonly string _connectionString;

	public AiReviewAuditStore(IConfiguration configuration)
	{
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
			((DbCommand)(object)command).CommandText = "CREATE TABLE IF NOT EXISTS ai_review_audits (\n  id INTEGER PRIMARY KEY AUTOINCREMENT,\n  document_id TEXT NOT NULL,\n  occurred_at TEXT NOT NULL,\n  model TEXT NOT NULL,\n  schema_version TEXT NOT NULL,\n  input_sha256 TEXT NOT NULL,\n  result_json TEXT NOT NULL\n);\nCREATE INDEX IF NOT EXISTS ix_ai_review_audits_document ON ai_review_audits(document_id, occurred_at);";
			await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
	}

	public async Task RecordAsync(Guid documentId, string model, string schemaVersion, string input, object result, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (documentId == Guid.Empty)
		{
			throw new ArgumentException("Eine Dokument-ID ist erforderlich.", "documentId");
		}
		using SHA256 sha = SHA256.Create();
		string inputHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty)));
		string json = JsonSerializer.Serialize(result);
		SqliteConnection connection = new SqliteConnection(_connectionString);
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "INSERT INTO ai_review_audits(document_id,occurred_at,model,schema_version,input_sha256,result_json) VALUES($document,$at,$model,$schema,$hash,$result)";
			command.Parameters.AddWithValue("$document", (object)documentId.ToString());
			command.Parameters.AddWithValue("$at", (object)DateTimeOffset.UtcNow.ToString("O"));
			command.Parameters.AddWithValue("$model", (object)model);
			command.Parameters.AddWithValue("$schema", (object)schemaVersion);
			command.Parameters.AddWithValue("$hash", (object)inputHash);
			command.Parameters.AddWithValue("$result", (object)json);
			await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
	}
}
