using System;

namespace NovaNein.Server;

public sealed record SapDocumentSnapshot(SapDocumentKind Kind, int DocEntry, int DocNum, string BusinessPartnerCode, string BusinessPartnerName, string InvoiceNumber, DateOnly DocumentDate, decimal GrossAmount, string Currency, int? AttachmentEntry, DateOnly? EntryDate = null, int? TransId = null, string? Comments = null);
