using System;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed record PdfInboxItem(Guid Id, string Sha256, string OriginalFileName, string Status, string? InvoiceNumber, string? BusinessPartner, decimal? GrossAmount, string? Currency, DateOnly? InvoiceDate, DocumentDirection? AssignedDirection, int? AssignedDocEntry, Guid? AssignedDocumentId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? AssignmentActor, DateTimeOffset? AssignedAt, string? LastError);
