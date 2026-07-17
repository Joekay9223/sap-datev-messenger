using System;
using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record PdfInboxSuggestion(Guid InboxId, SapDocumentKind Kind, string Direction, int DocEntry, int DocNum, string InvoiceNumber, string BusinessPartner, DateOnly DocumentDate, decimal GrossAmount, string Currency, decimal Confidence, IReadOnlyList<string> Reasons);
