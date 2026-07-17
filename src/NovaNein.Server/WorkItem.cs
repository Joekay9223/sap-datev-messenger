using System;

namespace NovaNein.Server;

public sealed record WorkItemAction(string Key, string Label);

public sealed record WorkItem(string Direction, string SapKind, int DocEntry, int DocNum, string InvoiceNumber, string BusinessPartner, DateOnly? DocumentDate, decimal? GrossAmount, string Currency, Guid? DocumentId, string PdfState, string ValidationState, string DatevState, string NextAction, bool Supported, string? Error, DateTimeOffset? UpdatedAt, DateOnly? EntryDate, string DocumentType, string OverallState, string OverallLabel, WorkItemStages Stages, IReadOnlyList<WorkItemAction>? Actions = null, bool Ignored = false, string? IgnoredReason = null, string? IgnoredBy = null, DateTimeOffset? IgnoredAt = null);
