namespace NovaNein.Server;

public sealed record WorkItemIgnoreEntry(
    SapDocumentKind SapKind,
    int DocEntry,
    int DocNum,
    string Reason,
    string IgnoredBy,
    DateTimeOffset IgnoredAt);
