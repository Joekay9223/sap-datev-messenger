namespace NovaNein.Server;

public sealed class PdfOrphanCleanupWorker(
    DocumentStore documents,
    PdfUploadStore uploads,
    PdfStorageCoordinator storageCoordinator,
    IConfiguration configuration,
    ILogger<PdfOrphanCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var grace = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Storage:OrphanGraceHours", 24), 1, 24 * 30));
        var interval = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Storage:OrphanCleanupIntervalHours", 24), 1, 24 * 7));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var storageLease = await storageCoordinator.EnterAsync(stoppingToken);
                var referenced = await documents.ListPdfHashesAsync(stoppingToken);
                var deleted = await uploads.CleanupOrphansAsync(referenced, grace, stoppingToken);
                if (deleted > 0) logger.LogWarning("{Count} nicht referenzierte PDF-Datei(en) wurden nach der Schonfrist entfernt.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Die Bereinigung nicht referenzierter PDF-Dateien ist fehlgeschlagen."); }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
