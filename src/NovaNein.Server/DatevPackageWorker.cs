using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NovaNein.Server;

public sealed class DatevPackageWorker(DocumentJobQueue jobs, DocumentStore documents, DatevPackageProcessor processor, ILogger<DatevPackageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await jobs.ClaimNextAsync(DocumentJobKind.CreateDatevPackage, stoppingToken);
            if (job is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
            try { await processor.ProcessAsync(job, stoppingToken); await jobs.CompleteAsync(job.Id, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "DATEV-Paketjob {JobId} fehlgeschlagen.", job.Id);
                await jobs.FailAsync(job.Id, ex.Message, documents, "datev-package-worker", stoppingToken);
            }
        }
    }
}
