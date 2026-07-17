using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class IncomingDocumentIntake(DocumentStore documents, DocumentJobQueue jobs)
{
    public async Task<DocumentRecord> AcceptAsync(SapDocumentIdentity sap, string pdfSha256, string originalFileName, string actor, CancellationToken cancellationToken = default)
    {
        return await jobs.CreateDocumentAndEnqueueAsync(documents, sap, pdfSha256, originalFileName, actor, DocumentJobKind.ValidateIncoming, cancellationToken);
    }

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var received = await documents.ListReceivedAsync(DocumentDirection.Incoming, cancellationToken);
        foreach (var document in received) await jobs.EnsureEnqueuedAsync(document.Id, DocumentJobKind.ValidateIncoming, cancellationToken);
        return received.Count;
    }
}

public sealed class OutgoingDocumentIntake(DocumentStore documents, DocumentJobQueue jobs)
{
    public async Task<DocumentRecord> AcceptAsync(SapDocumentIdentity sap, string pdfSha256, string originalFileName, string actor, CancellationToken cancellationToken = default)
    {
        if (sap.Direction != DocumentDirection.Outgoing) throw new ArgumentException("Der Ausgangsintake akzeptiert nur Ausgangsbelege.", nameof(sap));
        return await jobs.CreateDocumentAndEnqueueAsync(documents, sap, pdfSha256, originalFileName, actor, DocumentJobKind.ValidateOutgoing, cancellationToken);
    }

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var received = await documents.ListReceivedAsync(DocumentDirection.Outgoing, cancellationToken);
        foreach (var document in received) await jobs.EnsureEnqueuedAsync(document.Id, DocumentJobKind.ValidateOutgoing, cancellationToken);
        return received.Count;
    }
}
