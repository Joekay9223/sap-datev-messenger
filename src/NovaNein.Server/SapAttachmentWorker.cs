using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class SapAttachmentProcessor(DocumentStore documents, ISapServiceLayerClient sap, IConfiguration configuration)
{
    public async Task ProcessAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        if (job.Kind != DocumentJobKind.AttachToSap) throw new ArgumentException("Falscher Jobtyp für den SAP-Anhang.", nameof(job));
        if (!AutoAttachEnabled()) throw new InvalidOperationException("Der automatische SAP-Anhang ist nicht freigegeben.");
        var document = await documents.GetAsync(job.DocumentId, cancellationToken) ?? throw new InvalidOperationException("Beleg zum SAP-Anhangsjob fehlt.");
        if (document.Status == DocumentStatus.AttachedToSap) return;
        if (document.Status is not (DocumentStatus.Approved or DocumentStatus.Packaged)) throw new InvalidOperationException("Nur fachlich freigegebene Belege dürfen an SAP angehängt werden.");

        var root = configuration["Storage:DocumentRoot"] ?? "data/documents";
        var pdfPath = Path.Combine(root, $"{document.PdfSha256}.pdf");
        var kind = document.Sap.Direction == DocumentDirection.Incoming ? SapDocumentKind.PurchaseInvoice : SapDocumentKind.Invoice;
        await sap.AttachPdfAsync(kind, document.Sap.DocEntry, document.Sap.DocNum, pdfPath, cancellationToken);
        if (await documents.MarkAttachedToSapAsync(document.Id, "sap-attachment-worker", cancellationToken) is null)
            throw new InvalidOperationException("Der bestätigte SAP-Anhang konnte nicht atomar als NovaNein-Status gespeichert werden.");
    }

    public bool AutoAttachEnabled()
    {
        if (!configuration.GetValue("Sap:AutoAttachApprovedDocuments", false)) return false;
        if (!string.Equals(configuration["Sap:Mode"], "write-enabled", StringComparison.OrdinalIgnoreCase) || !configuration.GetValue("Sap:EnableAttachments2Writes", false)) return false;
        var sourceRoot = configuration["Sap:AttachmentSourceRoot"];
        if (string.IsNullOrWhiteSpace(sourceRoot)) return false;
        var documentRoot = configuration["Storage:DocumentRoot"] ?? "data/documents";
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var documents = Path.TrimEndingDirectorySeparator(Path.GetFullPath(documentRoot));
        return string.Equals(documents, source, StringComparison.OrdinalIgnoreCase) || documents.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SapAttachmentWorker(DocumentJobQueue jobs, DocumentStore documents, SapAttachmentProcessor processor, ILogger<SapAttachmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await jobs.ClaimNextAsync(DocumentJobKind.AttachToSap, stoppingToken);
            if (job is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
            try { await processor.ProcessAsync(job, stoppingToken); await jobs.CompleteAsync(job.Id, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SAP-Anhangsjob {JobId} fehlgeschlagen.", job.Id);
                await jobs.FailAsync(job.Id, ex.Message, documents, "sap-attachment-worker", stoppingToken);
            }
        }
    }
}
