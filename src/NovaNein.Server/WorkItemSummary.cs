namespace NovaNein.Server;

public sealed record WorkItemSummary(int Total, int Completed, int MissingPdf, int NeedsReview, int Blocked, int ReadyForDatev, int PackagesPrepared, int WatchfolderDelivered, int DatevUploaded, int DatevFinalized, int Ignored);
