using System.Text.Json.Serialization;

namespace NovaNein.Server;

public static class MailSourceStatuses
{
    public const string MailReceived = "MailReceived";
    public const string Extracted = "Extracted";
    public const string ProposalReady = "ProposalReady";
    public const string NeedsReview = "NeedsReview";
    public const string Blocked = "Blocked";
    public const string Approved = "Approved";
    public const string SapPosting = "SapPosting";
    public const string SapReadbackConfirmed = "SapReadbackConfirmed";
    public const string DatevPrepared = "DatevPrepared";
    public const string DatevFinalized = "DatevFinalized";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";
}

public sealed record MailAttachmentRecord(
    Guid Id,
    Guid MailSourceId,
    string GmailAttachmentId,
    string FileName,
    string MimeType,
    long Size,
    string Sha256,
    string LocalPath,
    string Status,
    string? Error);

public sealed record MailSourceRecord(
    Guid Id,
    string Mailbox,
    string GmailMessageId,
    string GmailThreadId,
    string HistoryId,
    string Subject,
    string Sender,
    DateTimeOffset ReceivedAt,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError,
    IReadOnlyList<MailAttachmentRecord>? Attachments = null);

public sealed record InvoiceProposalLine(
    int LineNumber,
    string Description,
    decimal NetAmount,
    decimal TaxAmount,
    decimal? TaxRate,
    string Account,
    string TaxCode,
    string SuggestionSource,
    decimal Confidence,
    bool LooksLikeGoods);

public sealed record InvoiceProposal(
    Guid Id,
    Guid MailSourceId,
    Guid MailAttachmentId,
    int Version,
    string Direction,
    string Status,
    string Signal,
    string DocumentType,
    string InvoiceNumber,
    string SupplierName,
    string? SupplierCode,
    string? SupplierVatId,
    string? SupplierTaxNumber,
    string? SupplierIban,
    DateOnly InvoiceDate,
    DateOnly? ServiceDate,
    DateOnly? DueDate,
    decimal NetAmount,
    decimal TaxAmount,
    decimal GrossAmount,
    string Currency,
    bool HasPurchaseOrderReference,
    bool HasGoodsCharacteristics,
    bool IsReverseCharge,
    string SuggestionReason,
    string SourceSha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? RejectionReason,
    IReadOnlyList<string> Findings,
    IReadOnlyList<InvoiceProposalLine> Lines,
    MailSourceRecord? MailSource = null,
    SapPostingResult? SapPosting = null);

public sealed record SupplierProposal(
    Guid Id,
    Guid InvoiceProposalId,
    int Version,
    string Status,
    string ProposedCardCode,
    string Name,
    string? VatId,
    string? TaxNumber,
    string? Iban,
    string? Street,
    string? PostalCode,
    string? City,
    string CountryCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? CreatedCardCode,
    string? LastError);

public sealed record SapPostingResult(
    Guid InvoiceProposalId,
    int DocEntry,
    int DocNum,
    int TransId,
    int AttachmentEntry,
    string ReadbackHash,
    DateTimeOffset PostedAt,
    string PostedBy);

public sealed record InvoiceProposalDecisionRequest(
    int ExpectedVersion,
    string Reason,
    IReadOnlyList<InvoiceProposalLineInput>? Lines = null);

public sealed record InvoiceProposalLineInput(
    int LineNumber,
    string Description,
    decimal NetAmount,
    decimal TaxAmount,
    string Account,
    string TaxCode);

public sealed record SupplierProposalApprovalRequest(
    int ExpectedVersion,
    string Reason,
    string? CardCode = null);

public sealed record GmailSyncStatus(
    bool Enabled,
    bool Configured,
    string Mailbox,
    string? HistoryId,
    DateTimeOffset? WatchExpiration,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset? LastSuccessfulSyncAt,
    string? LastError,
    int OpenProposals,
    int FailedMessages,
    int OrphanAttachments);

public sealed class SapOrphanAttachmentException(int attachmentEntry, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int AttachmentEntry { get; } = attachmentEntry;
}

public sealed record GmailCredentialSecret(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("refresh_token")] string RefreshToken);

public sealed record SapSupplierCandidate(
    string CardCode,
    string CardName,
    string? VatId,
    string? TaxNumber,
    string? Iban,
    string? Street,
    string? PostalCode,
    string? City,
    decimal MatchScore,
    IReadOnlyList<string> MatchReasons);

public sealed record SapCodingCandidate(
    string Account,
    string TaxCode,
    string Description,
    int UsageCount,
    decimal Confidence,
    string Source);

public sealed record SapSupplierCreateRequest(
    string CardCode,
    string Name,
    string? VatId,
    string? TaxNumber,
    string? Iban,
    string? Street,
    string? PostalCode,
    string? City,
    string CountryCode,
    string ProposalId);

public sealed record SapPurchaseInvoiceLineRequest(
    int LineNumber,
    string Description,
    decimal NetAmount,
    string Account,
    string TaxCode);

public sealed record SapPurchaseInvoiceRequest(
    Guid ProposalId,
    string SourceSha256,
    string SupplierCode,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly? ServiceDate,
    DateOnly? DueDate,
    string Currency,
    decimal GrossAmount,
    string PdfPath,
    IReadOnlyList<SapPurchaseInvoiceLineRequest> Lines);

public sealed record SapAccountValidation(
    string Account,
    bool Exists,
    bool Active,
    bool RequiresDimensions,
    string? Name,
    string? Error);

public sealed record SapTaxCodeValidation(
    string TaxCode,
    bool Exists,
    bool Active,
    string? Name,
    string? Error);
