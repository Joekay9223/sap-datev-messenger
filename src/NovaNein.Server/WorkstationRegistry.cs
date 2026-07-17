using Microsoft.Data.Sqlite;

namespace NovaNein.Server;

public sealed class WorkstationRegistry(IConfiguration configuration)
{
    private readonly string _connectionString = $"Data Source={configuration["Storage:DatabasePath"] ?? "data/novanein.db"}";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(_connectionString).DataSource);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS workstations (
              certificate_thumbprint TEXT PRIMARY KEY,
              workstation_name TEXT NOT NULL UNIQUE,
              registered_at TEXT NOT NULL,
              revoked_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS workstation_health (
              certificate_thumbprint TEXT PRIMARY KEY,
              workstation_name TEXT NOT NULL,
              client_version TEXT NOT NULL,
              client_kind TEXT NOT NULL,
              status TEXT NOT NULL,
              detail TEXT NOT NULL,
              last_seen_at TEXT NOT NULL,
              FOREIGN KEY(certificate_thumbprint) REFERENCES workstations(certificate_thumbprint)
            );
            CREATE TABLE IF NOT EXISTS workstation_health_events (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              certificate_thumbprint TEXT NOT NULL,
              workstation_name TEXT NOT NULL,
              client_version TEXT NOT NULL,
              client_kind TEXT NOT NULL,
              status TEXT NOT NULL,
              detail TEXT NOT NULL,
              occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_workstation_health_events_thumbprint_time
              ON workstation_health_events(certificate_thumbprint, occurred_at DESC, id DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RegisterAsync(string thumbprint, string workstationName, CancellationToken cancellationToken = default)
    {
        var normalizedThumbprint = NormalizeThumbprint(thumbprint);
        if (string.IsNullOrWhiteSpace(workstationName)) throw new ArgumentException("Der Arbeitsplatzname ist erforderlich.", nameof(workstationName));
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workstations(certificate_thumbprint,workstation_name,registered_at,revoked_at)
            VALUES($thumbprint,$name,$at,NULL)
            ON CONFLICT(workstation_name) DO UPDATE SET
              certificate_thumbprint=excluded.certificate_thumbprint,
              registered_at=excluded.registered_at,
              revoked_at=NULL
            ON CONFLICT(certificate_thumbprint) DO UPDATE SET
              workstation_name=excluded.workstation_name,
              registered_at=excluded.registered_at,
              revoked_at=NULL;
            """;
        command.Parameters.AddWithValue("$thumbprint", normalizedThumbprint); command.Parameters.AddWithValue("$name", workstationName.Trim()); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsRegisteredAsync(string? thumbprint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return false;
        string normalized;
        try { normalized = NormalizeThumbprint(thumbprint); } catch (ArgumentException) { return false; }
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM workstations WHERE certificate_thumbprint=$thumbprint AND revoked_at IS NULL)"; command.Parameters.AddWithValue("$thumbprint", normalized);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<bool> RevokeAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        var normalizedThumbprint = NormalizeThumbprint(thumbprint);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "UPDATE workstations SET revoked_at=$at WHERE certificate_thumbprint=$thumbprint AND revoked_at IS NULL"; command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$thumbprint", normalizedThumbprint);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<WorkstationHealthSnapshot> RecordHealthAsync(
        string thumbprint,
        ClientHealthReport report,
        CancellationToken cancellationToken = default)
    {
        var normalizedThumbprint = NormalizeThumbprint(thumbprint);
        var normalized = ClientHealthRules.Normalize(report);
        var occurredAt = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var nameCommand = connection.CreateCommand();
        nameCommand.Transaction = transaction;
        nameCommand.CommandText = "SELECT workstation_name FROM workstations WHERE certificate_thumbprint=$thumbprint AND revoked_at IS NULL";
        nameCommand.Parameters.AddWithValue("$thumbprint", normalizedThumbprint);
        var workstationName = await nameCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(workstationName))
            throw new InvalidOperationException("Der Arbeitsplatz ist nicht aktiv registriert.");

        var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO workstation_health(certificate_thumbprint,workstation_name,client_version,client_kind,status,detail,last_seen_at)
            VALUES($thumbprint,$name,$version,$kind,$status,$detail,$at)
            ON CONFLICT(certificate_thumbprint) DO UPDATE SET
              workstation_name=excluded.workstation_name,
              client_version=excluded.client_version,
              client_kind=excluded.client_kind,
              status=excluded.status,
              detail=excluded.detail,
              last_seen_at=excluded.last_seen_at;
            """;
        AddHealthParameters(upsert, normalizedThumbprint, workstationName, normalized, occurredAt);
        await upsert.ExecuteNonQueryAsync(cancellationToken);

        var history = connection.CreateCommand();
        history.Transaction = transaction;
        history.CommandText = """
            INSERT INTO workstation_health_events(certificate_thumbprint,workstation_name,client_version,client_kind,status,detail,occurred_at)
            VALUES($thumbprint,$name,$version,$kind,$status,$detail,$at);
            """;
        AddHealthParameters(history, normalizedThumbprint, workstationName, normalized, occurredAt);
        await history.ExecuteNonQueryAsync(cancellationToken);

        var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = """
            DELETE FROM workstation_health_events
            WHERE certificate_thumbprint=$thumbprint AND id NOT IN (
              SELECT id FROM workstation_health_events
              WHERE certificate_thumbprint=$thumbprint
              ORDER BY occurred_at DESC, id DESC LIMIT 200
            );
            """;
        prune.Parameters.AddWithValue("$thumbprint", normalizedThumbprint);
        await prune.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(normalizedThumbprint, workstationName, normalized.ClientVersion, normalized.ClientKind,
            normalized.Status, normalized.Detail ?? string.Empty, occurredAt);
    }

    public async Task<IReadOnlyList<WorkstationHealthSnapshot>> HealthHistoryAsync(
        string thumbprint,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var normalizedThumbprint = NormalizeThumbprint(thumbprint);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT certificate_thumbprint,workstation_name,client_version,client_kind,status,detail,occurred_at
            FROM workstation_health_events
            WHERE certificate_thumbprint=$thumbprint
            ORDER BY occurred_at DESC, id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$thumbprint", normalizedThumbprint);
        command.Parameters.AddWithValue("$limit", limit);
        var items = new List<WorkstationHealthSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6), null,
                    System.Globalization.DateTimeStyles.RoundtripKind)));
        return items;
    }

    private static void AddHealthParameters(
        SqliteCommand command,
        string thumbprint,
        string workstationName,
        ClientHealthReport report,
        DateTimeOffset occurredAt)
    {
        command.Parameters.AddWithValue("$thumbprint", thumbprint);
        command.Parameters.AddWithValue("$name", workstationName);
        command.Parameters.AddWithValue("$version", report.ClientVersion);
        command.Parameters.AddWithValue("$kind", report.ClientKind);
        command.Parameters.AddWithValue("$status", report.Status);
        command.Parameters.AddWithValue("$detail", report.Detail ?? string.Empty);
        command.Parameters.AddWithValue("$at", occurredAt.ToString("O"));
    }

    public static string NormalizeThumbprint(string thumbprint)
    {
        var normalized = new string((thumbprint ?? string.Empty).Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length is not (40 or 64)) throw new ArgumentException("Der Zertifikat-Thumbprint muss 40 oder 64 Hexadezimalzeichen enthalten.", nameof(thumbprint));
        return normalized;
    }
}
