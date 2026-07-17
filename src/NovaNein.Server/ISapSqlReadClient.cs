using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NovaNein.Server;

public interface ISapSqlReadClient
{
	Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<SapDocumentSnapshot>> ListDocumentsAsync(SapDocumentKind kind, DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.FromResult((IReadOnlyList<SapDocumentSnapshot>)Array.Empty<SapDocumentSnapshot>());
	}

	Task<SapDocumentSnapshot?> FindDocumentByDocNumAsync(SapDocumentKind kind, int docNum, CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.FromResult<SapDocumentSnapshot>(null);
	}

	Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default(CancellationToken));

	Task CheckReadinessAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task<SapAccountingDocument?> GetAccountingDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.FromResult<SapAccountingDocument>(null);
	}

	Task<IReadOnlyList<SapSupplierCandidate>> FindSuppliersAsync(
		string name,
		string? vatId,
		string? taxNumber,
		string? iban,
		string? street,
		string? postalCode,
		string? city,
		CancellationToken cancellationToken = default)
	{
		return Task.FromResult((IReadOnlyList<SapSupplierCandidate>)Array.Empty<SapSupplierCandidate>());
	}

	Task<IReadOnlyList<SapCodingCandidate>> GetSupplierCodingHistoryAsync(string cardCode, CancellationToken cancellationToken = default)
	{
		return Task.FromResult((IReadOnlyList<SapCodingCandidate>)Array.Empty<SapCodingCandidate>());
	}

	Task<SapAccountValidation> ValidateAccountAsync(string account, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(new SapAccountValidation(
			account,
			false,
			false,
			false,
			null,
			"Die Sachkontoprüfung ist für diese SAP-Lesequelle nicht verfügbar."));
	}

	Task<SapDocumentSnapshot?> FindPurchaseInvoiceDuplicateAsync(string cardCode, string invoiceNumber, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<SapDocumentSnapshot?>(null);
	}
}
