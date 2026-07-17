namespace NovaNein.Server;

public sealed class BackupWorker(DocumentStore documents, IConfiguration configuration, ILogger<BackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backupRoot = configuration["Backup:Directory"];
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            logger.LogWarning("NovaNein-Datensicherung ist nicht konfiguriert.");
            return;
        }
        if (!Path.IsPathFullyQualified(backupRoot))
        {
            logger.LogError("NovaNein-Sicherungsordner muss absolut sein.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CreateBackupAsync(backupRoot, stoppingToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                logger.LogError(ex, "NovaNein-Datensicherung ist fehlgeschlagen.");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    internal async Task<string> CreateBackupAsync(string backupRoot, CancellationToken cancellationToken = default)
    {
        var snapshot = Path.Combine(backupRoot, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(snapshot);
        await documents.BackupDatabaseAsync(Path.Combine(snapshot, "novanein.db"), cancellationToken);
        var sourceDocuments = configuration["Storage:DocumentRoot"] ?? "data/documents";
        if (Directory.Exists(sourceDocuments)) CopyDirectory(sourceDocuments, Path.Combine(snapshot, "documents"));
        var retention = Math.Max(1, configuration.GetValue("Backup:RetentionDays", 30));
        foreach (var directory in Directory.EnumerateDirectories(backupRoot).Where(path => Directory.GetCreationTimeUtc(path) < DateTime.UtcNow.AddDays(-retention)))
            Directory.Delete(directory, recursive: true);
        logger.LogInformation("NovaNein-Sicherung erstellt: {Snapshot}", snapshot);
        return snapshot;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
