using System;
using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record AccountingImportBatch(Guid Id, string OriginalFileName, string FileSha256, AccountingImportStatus Status, DateOnly? PeriodStart, DateOnly? PeriodEnd, int RowCount, int WarningCount, int ErrorCount, string ParserVersion, DateTimeOffset ImportedAt, string ImportedBy, DateTimeOffset? ConfirmedAt, string? ConfirmedBy, IReadOnlyList<AccountingImportIssue>? Issues = null, IReadOnlyList<DatevBookingRow>? Rows = null);
