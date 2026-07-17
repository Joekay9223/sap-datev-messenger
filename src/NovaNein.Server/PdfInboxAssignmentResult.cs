using NovaNein.Domain;

namespace NovaNein.Server;

public sealed record PdfInboxAssignmentResult(PdfInboxItem Inbox, DocumentRecord Document);
