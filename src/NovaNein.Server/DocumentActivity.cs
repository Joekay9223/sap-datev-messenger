using NovaNein.Domain;

namespace NovaNein.Server;

public sealed record DocumentActivity(
    Guid DocumentId,
    DateTimeOffset OccurredAt,
    string Kind,
    string Detail,
    string Actor,
    DocumentDirection Direction,
    int DocEntry,
    int DocNum,
    SapBusinessDocumentType SapKind,
    string OriginalFileName,
    DocumentStatus CurrentStatus,
    ReviewSignal? Signal);
