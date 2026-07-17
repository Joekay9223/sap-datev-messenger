namespace NovaNein.Server;

public sealed record WorkItemIgnoreAuditEntry(
    SapDocumentKind SapKind,
    int DocEntry,
    int DocNum,
    string Action,
    string Reason,
    string Actor,
    DateTimeOffset OccurredAt);
