namespace NovaNein.Server;

public sealed record SapJournalLine(int LineId, string Account, string CounterAccount, string DebitCredit, decimal Debit, decimal Credit, string Currency, string? CostCenter = null, string? Project = null);
