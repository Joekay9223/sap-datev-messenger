using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class WorkItemIgnoreStore
{
    private readonly string _connectionString;

    public WorkItemIgnoreStore(IConfiguration configuration)
    {
        _connectionString = "Data Source=" + (configuration["Storage:DatabasePath"] ?? "data/novanein.db");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(_connectionString).DataSource);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS work_item_ignores (
              sap_kind INTEGER NOT NULL,
              doc_entry INTEGER NOT NULL,
              doc_num INTEGER NOT NULL,
              reason TEXT NOT NULL,
              ignored_by TEXT NOT NULL,
              ignored_at TEXT NOT NULL,
              restored_by TEXT NULL,
              restored_at TEXT NULL,
              active INTEGER NOT NULL DEFAULT 1,
              PRIMARY KEY (sap_kind, doc_entry)
            );
            CREATE TABLE IF NOT EXISTS work_item_ignore_audit (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              sap_kind INTEGER NOT NULL,
              doc_entry INTEGER NOT NULL,
              doc_num INTEGER NOT NULL,
              action TEXT NOT NULL,
              reason TEXT NOT NULL,
              actor TEXT NOT NULL,
              occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_work_item_ignores_active
              ON work_item_ignores(active, sap_kind, doc_entry);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<(SapDocumentKind Kind, int DocEntry), WorkItemIgnoreEntry>> ListActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<(SapDocumentKind, int), WorkItemIgnoreEntry>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sap_kind,doc_entry,doc_num,reason,ignored_by,ignored_at
            FROM work_item_ignores
            WHERE active=1
            ORDER BY ignored_at;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = (SapDocumentKind)reader.GetInt32(0);
            if (!Enum.IsDefined(kind)) continue;
            var entry = new WorkItemIgnoreEntry(
                kind,
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)));
            entries[(entry.SapKind, entry.DocEntry)] = entry;
        }
        return entries;
    }

    public async Task<WorkItemIgnoreEntry> IgnoreAsync(
        SapDocumentKind kind,
        int docEntry,
        int docNum,
        string reason,
        string actor,
        CancellationToken cancellationToken = default)
    {
        Validate(kind, docEntry, reason, actor);
        var now = DateTimeOffset.UtcNow;
        var cleanReason = reason.Trim();
        var cleanActor = actor.Trim();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO work_item_ignores(
                  sap_kind,doc_entry,doc_num,reason,ignored_by,ignored_at,restored_by,restored_at,active)
                VALUES($kind,$entry,$num,$reason,$actor,$now,NULL,NULL,1)
                ON CONFLICT(sap_kind,doc_entry) DO UPDATE SET
                  doc_num=$num,
                  reason=$reason,
                  ignored_by=$actor,
                  ignored_at=$now,
                  restored_by=NULL,
                  restored_at=NULL,
                  active=1;
                """;
            command.Parameters.AddWithValue("$kind", (int)kind);
            command.Parameters.AddWithValue("$entry", docEntry);
            command.Parameters.AddWithValue("$num", Math.Max(0, docNum));
            command.Parameters.AddWithValue("$reason", cleanReason);
            command.Parameters.AddWithValue("$actor", cleanActor);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await AppendAuditAsync(connection, (SqliteTransaction)transaction, kind, docEntry, docNum, "Ignored", cleanReason, cleanActor, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new WorkItemIgnoreEntry(kind, docEntry, Math.Max(0, docNum), cleanReason, cleanActor, now);
    }

    public async Task<IReadOnlyList<WorkItemIgnoreAuditEntry>> HistoryAsync(
        SapDocumentKind kind,
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (docEntry <= 0) throw new ArgumentOutOfRangeException(nameof(docEntry));

        var entries = new List<WorkItemIgnoreAuditEntry>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sap_kind,doc_entry,doc_num,action,reason,actor,occurred_at
            FROM work_item_ignore_audit
            WHERE sap_kind=$kind AND doc_entry=$entry
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$entry", docEntry);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new WorkItemIgnoreAuditEntry(
                (SapDocumentKind)reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return entries;
    }

    public async Task<bool> RestoreAsync(
        SapDocumentKind kind,
        int docEntry,
        int docNum,
        string reason,
        string actor,
        CancellationToken cancellationToken = default)
    {
        Validate(kind, docEntry, reason, actor);
        var now = DateTimeOffset.UtcNow;
        var cleanReason = reason.Trim();
        var cleanActor = actor.Trim();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int changed;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE work_item_ignores
                SET active=0,restored_by=$actor,restored_at=$now
                WHERE sap_kind=$kind AND doc_entry=$entry AND active=1;
                """;
            command.Parameters.AddWithValue("$kind", (int)kind);
            command.Parameters.AddWithValue("$entry", docEntry);
            command.Parameters.AddWithValue("$actor", cleanActor);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            changed = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (changed > 0)
        {
            await AppendAuditAsync(connection, (SqliteTransaction)transaction, kind, docEntry, docNum, "Restored", cleanReason, cleanActor, now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return changed > 0;
    }

    private static async Task AppendAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SapDocumentKind kind,
        int docEntry,
        int docNum,
        string action,
        string reason,
        string actor,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO work_item_ignore_audit(sap_kind,doc_entry,doc_num,action,reason,actor,occurred_at)
            VALUES($kind,$entry,$num,$action,$reason,$actor,$at);
            """;
        audit.Parameters.AddWithValue("$kind", (int)kind);
        audit.Parameters.AddWithValue("$entry", docEntry);
        audit.Parameters.AddWithValue("$num", Math.Max(0, docNum));
        audit.Parameters.AddWithValue("$action", action);
        audit.Parameters.AddWithValue("$reason", reason);
        audit.Parameters.AddWithValue("$actor", actor);
        audit.Parameters.AddWithValue("$at", occurredAt.ToString("O"));
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(SapDocumentKind kind, int docEntry, string reason, string actor)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (docEntry <= 0) throw new ArgumentOutOfRangeException(nameof(docEntry), "Der SAP-Belegschlüssel muss größer als null sein.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3 || reason.Trim().Length > 500)
            throw new ArgumentException("Die Begründung muss zwischen 3 und 500 Zeichen lang sein.", nameof(reason));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Der ausführende Administrator fehlt.", nameof(actor));
    }
}
