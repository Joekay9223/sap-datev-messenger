namespace NovaNein.SapAddon;

// This wire contract mirrors NovaNein.Domain.DocumentStatus. Keep the explicit
// values because the net48 SAP client cannot reference the net8 domain assembly.
public enum NovaNeinDocumentStatus
{
    Received = 0,
    Validating = 1,
    NeedsReview = 2,
    Rejected = 3,
    Approved = 4,
    AttachedToSap = 5,
    Packaged = 6,
    Transferred = 7,
    Failed = 8
}

public enum SapDocumentDirection { Incoming, Outgoing }

public sealed class SapDocumentContext
{
    public SapDocumentContext(SapDocumentDirection direction, int docEntry, int docNum, string sapUser)
    {
        if (docEntry <= 0 || docNum <= 0) throw new ArgumentOutOfRangeException(nameof(docEntry));
        Direction = direction;
        DocEntry = docEntry;
        DocNum = docNum;
        SapUser = sapUser ?? string.Empty;
    }

    public SapDocumentDirection Direction { get; private set; }
    public int DocEntry { get; private set; }
    public int DocNum { get; private set; }
    public string SapUser { get; private set; }

    public bool IsSameDocument(SapDocumentContext? other) =>
        other is not null &&
        Direction == other.Direction &&
        DocEntry == other.DocEntry &&
        DocNum == other.DocNum &&
        string.Equals(SapUser, other.SapUser, StringComparison.Ordinal);
}

public enum DocumentContextMatch { Current, Missing, Changed }

public static class DocumentContextGuard
{
    public static DocumentContextMatch Compare(SapDocumentContext? selected, SapDocumentContext? active)
    {
        if (selected is null || active is null) return DocumentContextMatch.Missing;
        return selected.IsSameDocument(active) ? DocumentContextMatch.Current : DocumentContextMatch.Changed;
    }
}

public sealed record NovaNeinDocumentProgress(Guid Id, NovaNeinDocumentStatus Status, int? Signal)
{
    public bool IsTerminal => Status is
        NovaNeinDocumentStatus.NeedsReview or
        NovaNeinDocumentStatus.Rejected or
        NovaNeinDocumentStatus.Approved or
        NovaNeinDocumentStatus.AttachedToSap or
        NovaNeinDocumentStatus.Packaged or
        NovaNeinDocumentStatus.Transferred or
        NovaNeinDocumentStatus.Failed;
}

public sealed record NovaNeinDatevStatus(
    bool PdfArchived,
    DateTimeOffset? PackagePreparedAt,
    string? PackageFileName,
    string? PackageSha256,
    DateTimeOffset? UploadSucceededAt,
    DateTimeOffset? JobFinalizedAt,
    bool Transferred);

public sealed record NovaNeinArchivedPdf(string FileName, byte[] Content);

public enum DocumentProgressTone { Neutral, Yellow, Red, Green }

public readonly struct DocumentProgressPresentation
{
    public DocumentProgressPresentation(DocumentProgressTone tone, string message)
    {
        Tone = tone;
        Message = message;
    }

    public DocumentProgressTone Tone { get; }
    public string Message { get; }

    public static DocumentProgressPresentation For(NovaNeinDocumentStatus status) => status switch
    {
        NovaNeinDocumentStatus.NeedsReview => new(DocumentProgressTone.Yellow, "Prüfung gelb – manuelle Freigabe erforderlich."),
        NovaNeinDocumentStatus.Rejected => new(DocumentProgressTone.Red, "Prüfung rot – begründete manuelle Freigabe möglich."),
        NovaNeinDocumentStatus.Approved => new(DocumentProgressTone.Green, "Prüfung grün – fachlich freigegeben."),
        NovaNeinDocumentStatus.AttachedToSap => new(DocumentProgressTone.Green, "Geprüft und in SAP angehängt."),
        NovaNeinDocumentStatus.Failed => new(DocumentProgressTone.Red, "Verarbeitung fehlgeschlagen; bitte Serverstatus prüfen."),
        _ => new(DocumentProgressTone.Neutral, "Beleg übernommen – Prüfung läuft auf dem Server.")
    };
}
