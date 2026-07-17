namespace NovaNein.Domain;

public enum DocumentDirection { Incoming, Outgoing }
public enum DocumentStatus { Received, Validating, NeedsReview, Rejected, Approved, AttachedToSap, Packaged, Transferred, Failed }
public enum ReviewSignal { Green, Yellow, Red }

public enum SapBusinessDocumentType { Unspecified, PurchaseInvoice, Invoice, PurchaseCreditNote, CreditNote }

public sealed record SapDocumentIdentity(DocumentDirection Direction, int DocEntry, int DocNum, SapBusinessDocumentType Type = SapBusinessDocumentType.Unspecified);

public sealed record DocumentRecord(
    Guid Id,
    SapDocumentIdentity Sap,
    string PdfSha256,
    string OriginalFileName,
    DocumentStatus Status,
    ReviewSignal? Signal,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AuditEvent(Guid DocumentId, DateTimeOffset OccurredAt, string Kind, string Detail, string Actor);

public static class DocumentWorkflow
{
    public static bool CanTransition(DocumentStatus from, DocumentStatus to) => (from, to) switch
    {
        (DocumentStatus.Received, DocumentStatus.Validating) => true,
        (DocumentStatus.Validating, DocumentStatus.NeedsReview or DocumentStatus.Rejected or DocumentStatus.Approved) => true,
        (DocumentStatus.NeedsReview, DocumentStatus.Approved or DocumentStatus.Rejected) => true,
        (DocumentStatus.Rejected, DocumentStatus.Approved) => true,
        (DocumentStatus.Approved, DocumentStatus.AttachedToSap or DocumentStatus.Packaged) => true,
        (DocumentStatus.AttachedToSap, DocumentStatus.Packaged) => true,
        (DocumentStatus.Packaged, DocumentStatus.Transferred) => true,
        (_, DocumentStatus.Failed) => true,
        _ => false
    };

    public static bool MayCreateDatevPackage(DocumentRecord document) =>
        document.Status is (DocumentStatus.Approved or DocumentStatus.AttachedToSap)
        && document.Signal is (ReviewSignal.Green or ReviewSignal.Yellow or ReviewSignal.Red);
}
