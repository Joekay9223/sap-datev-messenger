using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class CompositeSapClient(ISapServiceLayerClient serviceLayerClient, ISapSqlReadClient sqlReadClient, IConfiguration configuration) : ISapServiceLayerClient
{
	private const string ServiceLayerMode = "service-layer";

	private const string SqlReadOnlyMode = "sql-read-only";

	public Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!UsesSqlReadOnly())
		{
			return serviceLayerClient.GetDocumentAsync(kind, docEntry, cancellationToken);
		}
		return sqlReadClient.GetDocumentAsync(kind, docEntry, cancellationToken);
	}

	public Task<SapDocumentSnapshot?> FindDocumentByDocNumAsync(SapDocumentKind kind, int docNum, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!UsesSqlReadOnly())
		{
			return serviceLayerClient.FindDocumentByDocNumAsync(kind, docNum, cancellationToken);
		}
		return sqlReadClient.FindDocumentByDocNumAsync(kind, docNum, cancellationToken);
	}

	public Task<IReadOnlyList<SapDocumentSnapshot>> ListDocumentsAsync(SapDocumentKind kind, DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!UsesSqlReadOnly())
		{
			return serviceLayerClient.ListDocumentsAsync(kind, fromEntryDate, toEntryDate, cancellationToken);
		}
		return sqlReadClient.ListDocumentsAsync(kind, fromEntryDate, toEntryDate, cancellationToken);
	}

	public Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!UsesSqlReadOnly())
		{
			return serviceLayerClient.FindMissingPdfAttachmentsAsync(fromEntryDate, toEntryDate, cancellationToken);
		}
		return sqlReadClient.FindMissingPdfAttachmentsAsync(fromEntryDate, toEntryDate, cancellationToken);
	}

	public Task CheckReadinessAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!UsesSqlReadOnly())
		{
			return serviceLayerClient.CheckReadinessAsync(cancellationToken);
		}
		return sqlReadClient.CheckReadinessAsync(cancellationToken);
	}

	public Task AttachPdfAsync(SapDocumentKind kind, int docEntry, int expectedDocNum, string localPdfPath, CancellationToken cancellationToken = default(CancellationToken))
	{
		return serviceLayerClient.AttachPdfAsync(kind, docEntry, expectedDocNum, localPdfPath, cancellationToken);
	}

	public Task<SapAccountingDocument?> GetAccountingDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!UsesSqlReadOnly())
		{
			return serviceLayerClient.GetAccountingDocumentAsync(kind, docEntry, cancellationToken);
		}
		return sqlReadClient.GetAccountingDocumentAsync(kind, docEntry, cancellationToken);
	}

	public Task<IReadOnlyList<SapSupplierCandidate>> FindSuppliersAsync(string name, string? vatId, string? taxNumber, string? iban, string? street, string? postalCode, string? city, CancellationToken cancellationToken = default)
		=> UsesSqlReadOnly()
			? sqlReadClient.FindSuppliersAsync(name, vatId, taxNumber, iban, street, postalCode, city, cancellationToken)
			: serviceLayerClient.FindSuppliersAsync(name, vatId, taxNumber, iban, street, postalCode, city, cancellationToken);

	public Task<IReadOnlyList<SapCodingCandidate>> GetSupplierCodingHistoryAsync(string cardCode, CancellationToken cancellationToken = default)
		=> UsesSqlReadOnly()
			? sqlReadClient.GetSupplierCodingHistoryAsync(cardCode, cancellationToken)
			: serviceLayerClient.GetSupplierCodingHistoryAsync(cardCode, cancellationToken);

	public Task<SapAccountValidation> ValidateAccountAsync(string account, CancellationToken cancellationToken = default)
		=> UsesSqlReadOnly()
			? sqlReadClient.ValidateAccountAsync(account, cancellationToken)
			: serviceLayerClient.ValidateAccountAsync(account, cancellationToken);

	public Task<SapTaxCodeValidation> ValidateTaxCodeAsync(string taxCode, CancellationToken cancellationToken = default)
		=> serviceLayerClient.ValidateTaxCodeAsync(taxCode, cancellationToken);

	public Task<string> CreateSupplierAsync(SapSupplierCreateRequest request, CancellationToken cancellationToken = default)
		=> serviceLayerClient.CreateSupplierAsync(request, cancellationToken);

	public Task<SapPostingResult> CreatePurchaseInvoiceAsync(SapPurchaseInvoiceRequest request, string actor, CancellationToken cancellationToken = default)
		=> serviceLayerClient.CreatePurchaseInvoiceAsync(request, actor, cancellationToken);

	public Task<SapPostingResult?> FindPurchaseInvoiceByProposalAsync(Guid proposalId, CancellationToken cancellationToken = default)
		=> serviceLayerClient.FindPurchaseInvoiceByProposalAsync(proposalId, cancellationToken);

	public Task<SapDocumentSnapshot?> FindPurchaseInvoiceDuplicateAsync(string cardCode, string invoiceNumber, CancellationToken cancellationToken = default)
		=> UsesSqlReadOnly()
			? sqlReadClient.FindPurchaseInvoiceDuplicateAsync(cardCode, invoiceNumber, cancellationToken)
			: serviceLayerClient.FindPurchaseInvoiceDuplicateAsync(cardCode, invoiceNumber, cancellationToken);

	private bool UsesSqlReadOnly()
	{
		string mode = configuration["Sap:ReadMode"]?.Trim();
		if (string.IsNullOrEmpty(mode) || string.Equals(mode, "service-layer", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (string.Equals(mode, "sql-read-only", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		throw new InvalidOperationException($"Ungültiger Sap:ReadMode '{mode}'. Erlaubt sind '{"service-layer"}' und '{"sql-read-only"}'.");
	}
}
