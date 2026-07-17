using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class IncomingValidationProcessor(DocumentStore documents, DocumentJobQueue jobs, ISapServiceLayerClient sap, IPdfInvoiceTextExtractor extractor, IConfiguration configuration)
{
    public async Task ProcessAsync(DocumentJob job, CancellationToken cancellationToken = default)
    {
        if (job.Kind is not (DocumentJobKind.ValidateIncoming or DocumentJobKind.ValidateOutgoing)) throw new ArgumentException("Falscher Jobtyp für die Belegvalidierung.", nameof(job));
        var document = await documents.GetAsync(job.DocumentId, cancellationToken) ?? throw new InvalidOperationException("Beleg zum Validierungsjob fehlt.");
        var expectedDirection = job.Kind == DocumentJobKind.ValidateIncoming ? DocumentDirection.Incoming : DocumentDirection.Outgoing;
        if (document.Sap.Direction != expectedDirection) throw new InvalidDataException("Jobrichtung und gespeicherter SAP-Beleg stimmen nicht überein.");
        // Ein Dienstabbruch kann nach der atomaren Ergebnisaufzeichnung, aber vor dem Jobabschluss erfolgen.
        // Beim Wiederanlauf ist die Validierung dann bereits vollständig und darf nicht erneut ausgeführt oder
        // nach mehreren wirkungslosen Wiederholungen auf Failed überschrieben werden.
        if (document.Status is DocumentStatus.Approved or DocumentStatus.NeedsReview or DocumentStatus.Rejected or DocumentStatus.AttachedToSap) return;
        if (document.Status != DocumentStatus.Received) throw new InvalidOperationException("Der Beleg ist nicht für eine automatische Validierung freigegeben.");
        var path = Path.Combine(configuration["Storage:DocumentRoot"] ?? "data/documents", $"{document.PdfSha256}.pdf");
        if (!File.Exists(path)) throw new FileNotFoundException("Original-PDF zum Validierungsjob fehlt.", path);
        var sapKind = document.Sap.Direction == DocumentDirection.Incoming ? SapDocumentKind.PurchaseInvoice : SapDocumentKind.Invoice;
        var sapSnapshot = await sap.GetDocumentAsync(sapKind, document.Sap.DocEntry, cancellationToken);
        if (sapSnapshot.DocNum != document.Sap.DocNum) throw new InvalidDataException("SAP-Dokumentnummer hat sich seit der Übernahme geändert.");
        // Die OpenAI-Interpretation bleibt unabhängig von SAP-Sollwerten. Erst dieses
        // lokale Vergleichsmodul stellt das strukturierte PDF-Ergebnis SAP gegenüber.
        var pdf = extractor.Extract(path, document.Sap.Direction);
        var sapFacts = new InvoiceFacts(sapSnapshot.InvoiceNumber, sapSnapshot.BusinessPartnerName, null, sapSnapshot.GrossAmount, sapSnapshot.Currency, sapSnapshot.DocumentDate, true, false);
        var pdfFacts = new InvoiceFacts(pdf.InvoiceNumber, pdf.BusinessPartnerName, pdf.VatId, pdf.GrossAmount, pdf.Currency, pdf.InvoiceDate, pdf.IsInvoice, pdf.HasRequiredFieldConflicts, pdf.IsDocumentQualityUncertain, pdf.HasReadableDocumentContent);
        var validation = InvoiceValidation.Compare(sapFacts, pdfFacts);
        var recorded = await documents.RecordValidationAsync(document.Id, validation, "validation-worker", cancellationToken) ?? throw new InvalidOperationException("Beleg war nicht mehr im erwarteten Empfangsstatus.");
        if (recorded.Status == DocumentStatus.Approved && SapAttachmentEnabled(configuration))
            await jobs.EnsureEnqueuedAsync(recorded.Id, DocumentJobKind.AttachToSap, cancellationToken);
        if (recorded.Status == DocumentStatus.Approved && configuration.GetValue("Datev:AutoPreparePackages", false))
            await jobs.EnsureEnqueuedAsync(recorded.Id, DocumentJobKind.CreateDatevPackage, cancellationToken);
    }

    private static bool SapAttachmentEnabled(IConfiguration configuration)
    {
        if (!configuration.GetValue("Sap:AutoAttachApprovedDocuments", false) || !string.Equals(configuration["Sap:Mode"], "write-enabled", StringComparison.OrdinalIgnoreCase) || !configuration.GetValue("Sap:EnableAttachments2Writes", false)) return false;
        var sourceRoot = configuration["Sap:AttachmentSourceRoot"];
        if (string.IsNullOrWhiteSpace(sourceRoot)) return false;
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var documents = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuration["Storage:DocumentRoot"] ?? "data/documents"));
        return string.Equals(documents, source, StringComparison.OrdinalIgnoreCase) || documents.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class IncomingValidationWorker(DocumentJobQueue jobs, DocumentStore documents, IncomingValidationProcessor processor, ILogger<IncomingValidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await jobs.ClaimNextAsync(DocumentJobKind.ValidateIncoming, stoppingToken)
                ?? await jobs.ClaimNextAsync(DocumentJobKind.ValidateOutgoing, stoppingToken);
            if (job is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
            try { await processor.ProcessAsync(job, stoppingToken); await jobs.CompleteAsync(job.Id, stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Validierungsjob {JobId} fehlgeschlagen.", job.Id);
                await jobs.FailAsync(job.Id, ex.Message, documents, "validation-worker", stoppingToken);
            }
        }
    }
}
