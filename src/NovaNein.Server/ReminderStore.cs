using Microsoft.Data.Sqlite;

namespace NovaNein.Server;

public sealed record WeeklyReminderSetting(string Recipient, bool Enabled);
public sealed record UserNotification(long Id, string Recipient, DateTimeOffset CreatedAt, string Title, string Body, bool IsRead);

public sealed class ReminderStore(IConfiguration configuration)
{
    private readonly string _connectionString = $"Data Source={configuration["Storage:DatabasePath"] ?? "data/novanein.db"}";
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = """
          CREATE TABLE IF NOT EXISTS weekly_reminder_settings(recipient TEXT PRIMARY KEY, enabled INTEGER NOT NULL, updated_at TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS user_notifications(id INTEGER PRIMARY KEY AUTOINCREMENT, recipient TEXT NOT NULL, created_at TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL, is_read INTEGER NOT NULL DEFAULT 0);
          CREATE TABLE IF NOT EXISTS reminder_runs(week_start TEXT PRIMARY KEY, completed_at TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS reminder_deliveries(week_start TEXT NOT NULL, recipient TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(week_start, recipient));
          CREATE INDEX IF NOT EXISTS ix_user_notifications_recipient_read ON user_notifications(recipient, is_read, id DESC);
          """; await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Creates the standard-on setting without overwriting an explicit user choice.
    /// This is called from the authenticated client-health path so a newly installed
    /// workstation receives the Monday reminder even before opening the settings tab.
    /// </summary>
    public async Task EnsureDefaultAsync(string recipient, CancellationToken ct = default)
    {
        ValidateRecipient(recipient);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO weekly_reminder_settings(recipient,enabled,updated_at) VALUES($recipient,1,$at) ON CONFLICT(recipient) DO NOTHING";
        command.Parameters.AddWithValue("$recipient", recipient.Trim());
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetEnabledAsync(string recipient, bool enabled, CancellationToken ct = default)
    {
        ValidateRecipient(recipient);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = "INSERT INTO weekly_reminder_settings(recipient,enabled,updated_at) VALUES($recipient,$enabled,$at) ON CONFLICT(recipient) DO UPDATE SET enabled=excluded.enabled,updated_at=excluded.updated_at";
        command.Parameters.AddWithValue("$recipient", recipient.Trim()); command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<WeeklyReminderSetting>> EnabledAsync(CancellationToken ct = default)
    {
        var result = new List<WeeklyReminderSetting>(); await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = "SELECT recipient,enabled FROM weekly_reminder_settings WHERE enabled=1";
        await using var reader = await command.ExecuteReaderAsync(ct); while(await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetInt32(1) != 0)); return result;
    }

    public async Task<bool> IsEnabledAsync(string recipient, CancellationToken ct = default)
    {
        ValidateRecipient(recipient);
        await EnsureDefaultAsync(recipient, ct);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled FROM weekly_reminder_settings WHERE recipient=$recipient";
        command.Parameters.AddWithValue("$recipient", recipient.Trim());
        var value = await command.ExecuteScalarAsync(ct);
        return value is not null && value != DBNull.Value && Convert.ToInt32(value) != 0;
    }
    /// <summary>
    /// Atomically claims a week for one recipient and creates its notification.
    /// A crash while processing another recipient can therefore be retried safely;
    /// the old global reminder_runs marker is retained only for backwards-compatible
    /// databases and is intentionally not used as the delivery gate anymore.
    /// </summary>
    public async Task<bool> AddWeeklyNotificationAsync(DateOnly weekStart, string recipient, string title, string body, CancellationToken ct = default)
    {
        ValidateRecipient(recipient);
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Der Hinweis-Titel ist erforderlich.", nameof(title));
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("Der Hinweistext ist erforderlich.", nameof(body));
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText = "INSERT OR IGNORE INTO reminder_deliveries(week_start,recipient,created_at) VALUES($week,$recipient,$at)";
        claim.Parameters.AddWithValue("$week", weekStart.ToString("yyyy-MM-dd"));
        claim.Parameters.AddWithValue("$recipient", recipient.Trim());
        claim.Parameters.AddWithValue("$at", now);
        if (await claim.ExecuteNonQueryAsync(ct) != 1)
        {
            transaction.Rollback();
            return false;
        }
        var notification = connection.CreateCommand();
        notification.Transaction = transaction;
        notification.CommandText = "INSERT INTO user_notifications(recipient,created_at,title,body,is_read) VALUES($recipient,$at,$title,$body,0)";
        notification.Parameters.AddWithValue("$recipient", recipient.Trim());
        notification.Parameters.AddWithValue("$at", now);
        notification.Parameters.AddWithValue("$title", title.Trim());
        notification.Parameters.AddWithValue("$body", body.Trim());
        await notification.ExecuteNonQueryAsync(ct);
        transaction.Commit();
        return true;
    }

    public async Task<bool> MarkReadAsync(long id, string recipient, CancellationToken ct = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        ValidateRecipient(recipient);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE user_notifications SET is_read=1 WHERE id=$id AND recipient=$recipient AND is_read=0";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$recipient", recipient.Trim());
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<UserNotification>> ListAsync(string recipient, CancellationToken ct = default)
    {
        ValidateRecipient(recipient);
        var result = new List<UserNotification>(); await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = "SELECT id,recipient,created_at,title,body,is_read FROM user_notifications WHERE recipient=$recipient ORDER BY id DESC LIMIT 100"; command.Parameters.AddWithValue("$recipient",recipient.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct); while(await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0),reader.GetString(1),DateTimeOffset.Parse(reader.GetString(2)),reader.GetString(3),reader.GetString(4),reader.GetInt32(5)!=0)); return result;
    }

    private static void ValidateRecipient(string recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient)) throw new ArgumentException("Der Benachrichtigungsempfänger ist erforderlich.", nameof(recipient));
        if (recipient.Length > 256) throw new ArgumentException("Der Benachrichtigungsempfänger ist zu lang.", nameof(recipient));
    }
}

public sealed class WeeklyReminderWorker(ReminderStore store, ISapServiceLayerClient sap, ILogger<WeeklyReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CreateMondayNotesAsync(ct); } catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { logger.LogWarning(ex, "Wochen-Reminder konnte nicht erstellt werden."); }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
    private async Task CreateMondayNotesAsync(CancellationToken ct)
    {
        var now = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "W. Europe Standard Time");
        if (now.DayOfWeek != DayOfWeek.Monday || now.Hour < 8) return;
        var recipients = await store.EnabledAsync(ct); if (recipients.Count == 0) return;
        var thisMonday = DateOnly.FromDateTime(now.Date).AddDays(-((int)now.DayOfWeek + 6) % 7);
        var previousMonday = thisMonday.AddDays(-7); var previousSunday = thisMonday.AddDays(-1);
        var gaps = await sap.FindMissingPdfAttachmentsAsync(previousMonday, previousSunday, ct);
        var body = gaps.Count == 0 ? $"Für den Eingabezeitraum {previousMonday:dd.MM.yyyy}–{previousSunday:dd.MM.yyyy} fehlen keine PDF-Anhänge." : string.Join(Environment.NewLine, gaps.Select(x => $"{x.EntryDate:dd.MM.yyyy}: {x.Kind} {x.DocNum} (DocEntry {x.DocEntry}) ohne PDF-Anhang."));
        foreach (var recipient in recipients)
            await store.AddWeeklyNotificationAsync(previousMonday, recipient.Recipient, "NovaNein Wochen-Reminder", body, ct);
    }
}
