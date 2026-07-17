using System;
using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record ReconciliationItem(string Id, Guid BatchId, SapDocumentKind? SapKind, string? Direction, int? DocEntry, int? DocNum, string InvoiceNumber, string BusinessPartner, DateOnly? DocumentDate, decimal? SapAmount, string SapCurrency, Guid? DatevRowId, decimal? DatevAmount, string DatevCurrency, string DatevAccount, string DatevReference, ReconciliationStatus Status, IReadOnlyList<string> Reasons, string ExpectedHash, bool PdfPresent, DateTimeOffset? DecidedAt = null, string? DecidedBy = null, string? DecisionReason = null);
