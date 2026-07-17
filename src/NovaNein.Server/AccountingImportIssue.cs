namespace NovaNein.Server;

public sealed record AccountingImportIssue(AccountingIssueSeverity Severity, int? RowNumber, string Message);
