namespace NovaNein.Server;

public sealed record DocumentStatistics(int Total, int Received, int NeedsReview, int Approved, int Rejected, int Failed, int AttachedToSap, int CreatedLast7Days, int CreatedLast30Days);
