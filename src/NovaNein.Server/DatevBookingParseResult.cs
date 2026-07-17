using System;
using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record DatevBookingParseResult(IReadOnlyList<DatevBookingRowDraft> Rows, IReadOnlyList<AccountingImportIssue> Issues, DateOnly? PeriodStart, DateOnly? PeriodEnd, string EncodingName, char Delimiter);
