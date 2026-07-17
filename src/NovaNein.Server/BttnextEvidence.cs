using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using NovaNein.Domain;

namespace NovaNein.Server;

public enum BttnextEventType { UploadSucceeded, JobFinalized }

public sealed record BttnextLogEvent(BttnextEventType Type, string PackageFileName, DateTimeOffset OccurredAt, string RawLine);

public sealed record TransferEvidence(Guid DocumentId, string PackageSha256, string PackageFileName, DateTimeOffset? PackagePreparedAt, DateTimeOffset? UploadSucceededAt, DateTimeOffset? JobFinalizedAt)
{
    public bool IsTransferred => UploadSucceededAt is not null && JobFinalizedAt is not null;
}

public static partial class BttnextLogParser
{
    [GeneratedRegex(@"\b(UploadSucceeded|JobFinalized)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventPattern();
    [GeneratedRegex(@"\bstate\s*=\s*(JobDeletedOrMoved|FileDeletedOrMoved|JobProtocolEntriesSuccess)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObservedStatePattern();
    [GeneratedRegex("\\b(?:PackageFileName|FileName|Package|File)\\s*[=:]\\s*[\\\"']?([^;\\\"'\\s]+\\.zip)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();
    [GeneratedRegex(@"^(\d{2}\.\d{2}\.\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    public static BttnextLogEvent? Parse(string line, DateTimeOffset fallbackOccurredAt, TimeZoneInfo? timeZone = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var packageName = PackagePattern().Match(line).Groups[1].Value;
        if (string.IsNullOrEmpty(packageName)) return null;
        var eventName = EventPattern().Match(line).Groups[1].Value;
        var observedState = ObservedStatePattern().Match(line).Groups[1].Value;
        var type = eventName.Equals("UploadSucceeded", StringComparison.OrdinalIgnoreCase)
            || observedState.Equals("JobDeletedOrMoved", StringComparison.OrdinalIgnoreCase)
            || observedState.Equals("FileDeletedOrMoved", StringComparison.OrdinalIgnoreCase)
                ? BttnextEventType.UploadSucceeded
                : eventName.Equals("JobFinalized", StringComparison.OrdinalIgnoreCase)
                  || observedState.Equals("JobProtocolEntriesSuccess", StringComparison.OrdinalIgnoreCase)
                    ? BttnextEventType.JobFinalized
                    : (BttnextEventType?)null;
        if (type is null) return null;

        var occurredAt = ParseTimestamp(line, fallbackOccurredAt, timeZone ?? TimeZoneInfo.Local);
        return new(type.Value, Path.GetFileName(packageName), occurredAt, line);
    }

    private static DateTimeOffset ParseTimestamp(string line, DateTimeOffset fallback, TimeZoneInfo timeZone)
    {
        var value = TimestampPattern().Match(line).Groups[1].Value;
        if (!DateTime.TryParseExact(value, "dd.MM.yy HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return fallback;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }
}

public sealed class TransferEvidenceStore(IConfiguration configuration)
{
    private readonly string _connectionString = $"Data Source={configuration["Storage:DatabasePath"] ?? "data/novanein.db"}";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(_connectionString).DataSource);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = """
            CREATE TABLE IF NOT EXISTS transfer_packages (
              document_id TEXT PRIMARY KEY,
              zip_sha256 TEXT NOT NULL UNIQUE,
              package_file_name TEXT NOT NULL UNIQUE,
              bttnext_package_file_name TEXT NULL UNIQUE,
              package_prepared_at TEXT NULL,
              upload_succeeded_at TEXT NULL,
              job_finalized_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS bttnext_archive_cache (
              archive_path TEXT PRIMARY KEY,
              file_size INTEGER NOT NULL,
              last_write_utc_ticks INTEGER NOT NULL,
              zip_sha256 TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS bttnext_log_cursors (
              log_path TEXT PRIMARY KEY,
              byte_offset INTEGER NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS bttnext_monitor_state (
              singleton INTEGER PRIMARY KEY CHECK(singleton=1),
              initialized_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS bttnext_events (
              fingerprint TEXT PRIMARY KEY,
              event_type INTEGER NOT NULL,
              package_file_name TEXT NOT NULL,
              occurred_at TEXT NOT NULL,
              raw_line TEXT NOT NULL,
              observed_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "transfer_packages", "bttnext_package_file_name", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "transfer_packages", "package_prepared_at", "TEXT NULL", cancellationToken);
        var index = connection.CreateCommand(); index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_transfer_packages_bttnext_package_file_name ON transfer_packages(bttnext_package_file_name)"; await index.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RegisterPackageAsync(Guid documentId, string zipSha256, string packageFileName, DateTimeOffset? preparedAt = null, CancellationToken cancellationToken = default)
    {
        var hash = NormalizeHash(zipSha256);
        if (!string.Equals(packageFileName, Path.GetFileName(packageFileName), StringComparison.Ordinal) || !packageFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Der Paketname muss ein reiner ZIP-Dateiname sein.", nameof(packageFileName));
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "INSERT INTO transfer_packages(document_id,zip_sha256,package_file_name,package_prepared_at) VALUES($document,$hash,$file,$prepared)";
        command.Parameters.AddWithValue("$document", documentId.ToString()); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$file", packageFileName);
        command.Parameters.AddWithValue("$prepared", (preparedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RecordAsync(BttnextLogEvent eventData, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var fingerprint = Fingerprint(eventData);
        var insert = connection.CreateCommand(); insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = "INSERT OR IGNORE INTO bttnext_events(fingerprint,event_type,package_file_name,occurred_at,raw_line,observed_at) VALUES($fingerprint,$type,$file,$occurred,$line,$observed)";
        insert.Parameters.AddWithValue("$fingerprint", fingerprint); insert.Parameters.AddWithValue("$type", (int)eventData.Type); insert.Parameters.AddWithValue("$file", eventData.PackageFileName);
        insert.Parameters.AddWithValue("$occurred", eventData.OccurredAt.ToString("O")); insert.Parameters.AddWithValue("$line", SafeRawLine(eventData.RawLine)); insert.Parameters.AddWithValue("$observed", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await ReconcilePackageAsync(connection, (SqliteTransaction)transaction, eventData.PackageFileName, null, cancellationToken);
        var eligibility = connection.CreateCommand(); eligibility.Transaction = (SqliteTransaction)transaction;
        eligibility.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM transfer_packages p
              JOIN datev_transfer_requests r ON r.document_id=p.document_id AND r.package_sha256=p.zip_sha256
              WHERE (p.package_file_name=$file OR p.bttnext_package_file_name=$file)
                AND r.status IN ('watchfolder-delivered','awaiting-datev-confirmation','finalized')
                AND r.watchfolder_delivered_at IS NOT NULL AND $occurred >= r.watchfolder_delivered_at
            )
            """;
        eligibility.Parameters.AddWithValue("$file", eventData.PackageFileName);
        eligibility.Parameters.AddWithValue("$occurred", eventData.OccurredAt.ToString("O"));
        var matched = Convert.ToInt32(await eligibility.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        await transaction.CommitAsync(cancellationToken);
        return matched;
    }

    public async Task<bool> ReconcileDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("Eine Dokument-ID ist erforderlich.", nameof(documentId));
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var matched = await ReconcilePackageAsync(connection, (SqliteTransaction)transaction, null, documentId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return matched;
    }

    private static async Task<bool> ReconcilePackageAsync(SqliteConnection connection, SqliteTransaction transaction, string? packageFileName, Guid? documentId, CancellationToken cancellationToken)
    {
        var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """
            UPDATE transfer_packages AS p
            SET upload_succeeded_at=COALESCE(upload_succeeded_at,(
                  SELECT MIN(e.occurred_at) FROM bttnext_events e
                  JOIN datev_transfer_requests r ON r.document_id=p.document_id AND r.package_sha256=p.zip_sha256
                  WHERE e.event_type=$upload
                    AND (e.package_file_name=p.package_file_name OR e.package_file_name=p.bttnext_package_file_name)
                    AND r.status IN ('watchfolder-delivered','awaiting-datev-confirmation','finalized')
                    AND r.watchfolder_delivered_at IS NOT NULL
                    AND e.occurred_at >= r.watchfolder_delivered_at
                )),
                job_finalized_at=COALESCE(job_finalized_at,(
                  SELECT MIN(e.occurred_at) FROM bttnext_events e
                  JOIN datev_transfer_requests r ON r.document_id=p.document_id AND r.package_sha256=p.zip_sha256
                  WHERE e.event_type=$final
                    AND (e.package_file_name=p.package_file_name OR e.package_file_name=p.bttnext_package_file_name)
                    AND r.status IN ('watchfolder-delivered','awaiting-datev-confirmation','finalized')
                    AND r.watchfolder_delivered_at IS NOT NULL
                    AND e.occurred_at >= r.watchfolder_delivered_at
                ))
            WHERE ($file IS NULL OR p.package_file_name=$file OR p.bttnext_package_file_name=$file)
              AND ($document IS NULL OR p.document_id=$document)
              AND EXISTS (
                SELECT 1 FROM datev_transfer_requests r
                WHERE r.document_id=p.document_id AND r.package_sha256=p.zip_sha256
                  AND r.status IN ('watchfolder-delivered','awaiting-datev-confirmation','finalized')
                  AND r.watchfolder_delivered_at IS NOT NULL
              )
            """;
        update.Parameters.AddWithValue("$upload", (int)BttnextEventType.UploadSucceeded);
        update.Parameters.AddWithValue("$final", (int)BttnextEventType.JobFinalized);
        update.Parameters.AddWithValue("$file", (object?)packageFileName ?? DBNull.Value);
        update.Parameters.AddWithValue("$document", documentId is null ? DBNull.Value : documentId.Value.ToString());
        var matched = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (matched) await FinalizeCompletedTransferAsync(connection, transaction, packageFileName, documentId, cancellationToken);
        return matched;
    }

    private static async Task FinalizeCompletedTransferAsync(SqliteConnection connection, SqliteTransaction transaction, string? packageFileName, Guid? requestedDocumentId, CancellationToken cancellationToken)
    {
        var find = connection.CreateCommand(); find.Transaction = transaction;
        find.CommandText = """
            SELECT p.document_id,p.package_file_name,p.zip_sha256,p.upload_succeeded_at,p.job_finalized_at
            FROM transfer_packages p
            JOIN datev_transfer_requests r ON r.document_id=p.document_id AND r.package_sha256=p.zip_sha256
            WHERE ($file IS NULL OR p.package_file_name=$file OR p.bttnext_package_file_name=$file)
              AND ($document IS NULL OR p.document_id=$document)
              AND p.upload_succeeded_at IS NOT NULL AND p.job_finalized_at IS NOT NULL
            """;
        find.Parameters.AddWithValue("$file", (object?)packageFileName ?? DBNull.Value);
        find.Parameters.AddWithValue("$document", requestedDocumentId is null ? DBNull.Value : requestedDocumentId.Value.ToString());
        await using var reader = await find.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var documentId = reader.GetString(0); var canonicalName = reader.GetString(1); var hash = reader.GetString(2);
        var uploadAt = reader.GetString(3); var finalizedAt = reader.GetString(4);
        await reader.DisposeAsync();

        var request = connection.CreateCommand(); request.Transaction = transaction;
        request.CommandText = "UPDATE datev_transfer_requests SET status='finalized',last_error=NULL WHERE document_id=$document AND package_sha256=$hash AND status IN ('watchfolder-delivered','awaiting-datev-confirmation','finalized')";
        request.Parameters.AddWithValue("$document", documentId); request.Parameters.AddWithValue("$hash", hash); await request.ExecuteNonQueryAsync(cancellationToken);

        var document = connection.CreateCommand(); document.Transaction = transaction;
        document.CommandText = "UPDATE documents SET status=$transferred,updated_at=$at WHERE id=$document AND status IN ($packaged,$transferred)";
        document.Parameters.AddWithValue("$transferred", (int)DocumentStatus.Transferred); document.Parameters.AddWithValue("$packaged", (int)DocumentStatus.Packaged);
        document.Parameters.AddWithValue("$at", finalizedAt); document.Parameters.AddWithValue("$document", documentId); await document.ExecuteNonQueryAsync(cancellationToken);

        var audit = connection.CreateCommand(); audit.Transaction = transaction;
        audit.CommandText = "INSERT INTO audit_events(document_id,occurred_at,kind,detail,actor) SELECT $document,$at,'DatevTransferCompleted',$detail,'bttnext-monitor' WHERE NOT EXISTS(SELECT 1 FROM audit_events WHERE document_id=$document AND kind='DatevTransferCompleted' AND detail=$detail)";
        audit.Parameters.AddWithValue("$document", documentId); audit.Parameters.AddWithValue("$at", finalizedAt);
        audit.Parameters.AddWithValue("$detail", $"DATEV-Übertragung bestätigt: {canonicalName} ({hash}), UploadSucceeded={uploadAt}, JobFinalized={finalizedAt}.");
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TransferEvidence?> GetAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT document_id,zip_sha256,package_file_name,package_prepared_at,upload_succeeded_at,job_finalized_at FROM transfer_packages WHERE document_id=$id"; command.Parameters.AddWithValue("$id", documentId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), ReadDate(reader, 3), ReadDate(reader, 4), ReadDate(reader, 5)) : null;
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "DELETE FROM transfer_packages"; await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InitializeLogCursorsAsync(string logDirectory, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var exists = connection.CreateCommand(); exists.CommandText = "SELECT COUNT(*) FROM bttnext_monitor_state WHERE singleton=1";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1) return;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var path in Directory.EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            var cursor = connection.CreateCommand(); cursor.Transaction = (SqliteTransaction)transaction;
            cursor.CommandText = "INSERT OR REPLACE INTO bttnext_log_cursors(log_path,byte_offset,updated_at) VALUES($path,$offset,$at)";
            cursor.Parameters.AddWithValue("$path", Path.GetFullPath(path)); cursor.Parameters.AddWithValue("$offset", new FileInfo(path).Length); cursor.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await cursor.ExecuteNonQueryAsync(cancellationToken);
        }
        var state = connection.CreateCommand(); state.Transaction = (SqliteTransaction)transaction;
        state.CommandText = "INSERT INTO bttnext_monitor_state(singleton,initialized_at) VALUES(1,$at)"; state.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); await state.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long?> GetLogCursorAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT byte_offset FROM bttnext_log_cursors WHERE log_path=$path"; command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        var result = await command.ExecuteScalarAsync(cancellationToken); return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task SaveLogCursorAsync(string path, long byteOffset, CancellationToken cancellationToken = default)
    {
        if (byteOffset < 0) throw new ArgumentOutOfRangeException(nameof(byteOffset));
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "INSERT INTO bttnext_log_cursors(log_path,byte_offset,updated_at) VALUES($path,$offset,$at) ON CONFLICT(log_path) DO UPDATE SET byte_offset=$offset,updated_at=$at";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path)); command.Parameters.AddWithValue("$offset", byteOffset); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> ReconcileArchiveAsync(string archiveDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archiveDirectory) || !Path.IsPathFullyQualified(archiveDirectory)) throw new ArgumentException("Das BTTnext-Archivverzeichnis muss absolut sein.", nameof(archiveDirectory));
        if (!Directory.Exists(archiveDirectory)) return 0;
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var cache = new Dictionary<string, (long Length, long LastWriteTicks, string Hash)>(StringComparer.OrdinalIgnoreCase);
        var load = connection.CreateCommand(); load.CommandText = "SELECT archive_path,file_size,last_write_utc_ticks,zip_sha256 FROM bttnext_archive_cache";
        await using (var reader = await load.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) cache[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
        var mapped = 0;
        foreach (var path in Directory.EnumerateFiles(archiveDirectory, "*.zip", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path); var ticks = file.LastWriteTimeUtc.Ticks;
            if (!cache.TryGetValue(path, out var known) || known.Length != file.Length || known.LastWriteTicks != ticks)
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sha = SHA256.Create(); known = (file.Length, ticks, Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken)));
                var save = connection.CreateCommand(); save.CommandText = "INSERT INTO bttnext_archive_cache(archive_path,file_size,last_write_utc_ticks,zip_sha256) VALUES($path,$size,$ticks,$hash) ON CONFLICT(archive_path) DO UPDATE SET file_size=$size,last_write_utc_ticks=$ticks,zip_sha256=$hash"; save.Parameters.AddWithValue("$path", path); save.Parameters.AddWithValue("$size", known.Length); save.Parameters.AddWithValue("$ticks", known.LastWriteTicks); save.Parameters.AddWithValue("$hash", known.Hash); await save.ExecuteNonQueryAsync(cancellationToken);
            }
            var link = connection.CreateCommand(); link.CommandText = "UPDATE transfer_packages SET bttnext_package_file_name=$file WHERE zip_sha256=$hash AND (bttnext_package_file_name IS NULL OR bttnext_package_file_name=$file)"; link.Parameters.AddWithValue("$file", file.Name); link.Parameters.AddWithValue("$hash", known.Hash);
            mapped += await link.ExecuteNonQueryAsync(cancellationToken);
        }
        return mapped;
    }

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var check = connection.CreateCommand(); check.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync(); var alter = connection.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}"; await alter.ExecuteNonQueryAsync(cancellationToken);
    }
    private static string NormalizeHash(string value)
    {
        var hash = new string((value ?? string.Empty).Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        if (hash.Length != 64) throw new ArgumentException("Die ZIP-SHA-256-Prüfsumme muss 64 Hexadezimalzeichen enthalten.", nameof(value));
        return hash;
    }
    private static string Fingerprint(BttnextLogEvent eventData)
    {
        var bytes = Encoding.UTF8.GetBytes($"{(int)eventData.Type}|{eventData.PackageFileName}|{eventData.OccurredAt:O}|{eventData.RawLine}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    private static string SafeRawLine(string value)
    {
        value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 2000 ? value : value[..2000];
    }
}
