using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class PdfInboxService(PdfInboxStore inbox, PdfUploadStore uploads, PdfStorageCoordinator storageCoordinator, ISapServiceLayerClient sap, DocumentStore documents, IncomingDocumentIntake incoming, OutgoingDocumentIntake outgoing, IPdfInvoiceTextExtractor extractor)
{
	public async Task<PdfInboxItem> UploadAsync(string originalFileName, long declaredLength, Stream content, string actor, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await UploadAsync(originalFileName, declaredLength, content, actor, extractFacts: true, cancellationToken);
	}

	public async Task<PdfInboxItem> UploadAsync(string originalFileName, long declaredLength, Stream content, string actor, bool extractFacts, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(actor))
		{
			throw new ArgumentException("Ein Akteur ist erforderlich.", "actor");
		}
		ArgumentNullException.ThrowIfNull(content, "content");
		PdfInboxItem result;
		await using (await storageCoordinator.EnterAsync(cancellationToken))
		{
			PdfUploadStoreResult stored = await uploads.StoreAsync(originalFileName, declaredLength, content, cancellationToken);
			if ((await documents.ListPdfHashesAsync(cancellationToken)).Contains(stored.Sha256))
			{
				throw new PdfInboxDuplicateException("Diese PDF ist bereits einem NovaNein-Beleg zugeordnet.");
			}
			ExtractedInvoiceFacts facts = null;
			if (extractFacts)
			{
				try
				{
					facts = extractor.Extract(stored.Path);
				}
				catch (Exception exception) when (IsRecoverableExtractionFailure(exception))
				{
				}
			}
			result = await inbox.CreateAsync(stored.Sha256, Path.GetFileName(originalFileName), facts, cancellationToken, actor);
		}
		return result;
	}

	private static bool IsRecoverableExtractionFailure(Exception exception)
	{
		if (!(exception is OperationCanceledException))
		{
			return !(exception is OutOfMemoryException);
		}
		return false;
	}

	public Task<IReadOnlyList<PdfInboxItem>> ListAsync(PdfInboxStatus? status = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		return inbox.ListAsync(status, cancellationToken);
	}

	public async Task<IReadOnlyList<PdfInboxSuggestion>> SuggestAsync(Guid inboxId, CancellationToken cancellationToken = default(CancellationToken))
	{
		PdfInboxItem item = (await inbox.GetAsync(inboxId, cancellationToken)) ?? throw new KeyNotFoundException("Die PDF im Eingang wurde nicht gefunden.");
		if (!string.Equals(item.Status, "unassigned", StringComparison.OrdinalIgnoreCase))
		{
			return Array.Empty<PdfInboxSuggestion>();
		}
		DateOnly center = item.InvoiceDate ?? DateOnly.FromDateTime(DateTime.Today);
		IReadOnlyList<SapAttachmentGap> gaps = await sap.FindMissingPdfAttachmentsAsync(center.AddDays(-45), center.AddDays(45), cancellationToken);
		List<PdfInboxSuggestion> candidates = new List<PdfInboxSuggestion>();
		foreach (SapAttachmentGap gap in gaps)
		{
			SapDocumentSnapshot snapshot = await TryGetSnapshotAsync(gap.Kind, gap.DocEntry, cancellationToken);
			if ((object)snapshot != null)
			{
				IReadOnlyList<string> reasons;
				decimal score = Score(item, snapshot, out reasons);
				if (!(score < 0.25m))
				{
					candidates.Add(new PdfInboxSuggestion(inboxId, gap.Kind, (DirectionFor(gap.Kind) == DocumentDirection.Incoming) ? "incoming" : "outgoing", snapshot.DocEntry, snapshot.DocNum, snapshot.InvoiceNumber, snapshot.BusinessPartnerName, snapshot.DocumentDate, snapshot.GrossAmount, snapshot.Currency, Math.Round(score, 3), reasons));
				}
			}
		}
		return (from x in candidates
			orderby x.Confidence descending, x.DocumentDate
			select x).Take(25).ToArray();
	}

	public Task<IReadOnlyList<PdfInboxSuggestion>> GetSuggestionsAsync(Guid inboxId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return SuggestAsync(inboxId, cancellationToken);
	}

	public async Task<PdfInboxAssignmentResult> AssignAsync(Guid inboxId, SapDocumentKind kind, int docEntry, int expectedDocNum, string actor, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (docEntry <= 0)
		{
			throw new ArgumentOutOfRangeException("docEntry");
		}
		if (expectedDocNum <= 0)
		{
			throw new ArgumentOutOfRangeException("expectedDocNum");
		}
		if (string.IsNullOrWhiteSpace(actor))
		{
			throw new ArgumentException("Ein Akteur ist erforderlich.", "actor");
		}
		DocumentDirection direction = DirectionFor(kind);
		PdfInboxItem item = (await inbox.GetAsync(inboxId, cancellationToken)) ?? throw new KeyNotFoundException("Die PDF im Eingang wurde nicht gefunden.");
		if (!string.Equals(item.Status, "unassigned", StringComparison.OrdinalIgnoreCase))
		{
			throw new PdfInboxAlreadyAssignedException("Die PDF wurde bereits zugeordnet oder abgelehnt.");
		}
		if ((await sap.GetDocumentAsync(kind, docEntry, cancellationToken)).DocNum != expectedDocNum)
		{
			throw new InvalidOperationException("Die übermittelte SAP-Belegnummer stimmt nicht mit dem aktuellen SAP-Lesestand überein.");
		}
		PdfInboxAssignmentResult result;
		await using (await storageCoordinator.EnterAsync(cancellationToken))
		{
			if ((object)(await documents.GetBySapAsync(direction, kind.ToDomain(), docEntry, cancellationToken)) != null)
			{
				throw new InvalidOperationException("Dieser SAP-Beleg ist bereits mit einer NovaNein-PDF verknüpft.");
			}
			if ((await documents.ListPdfHashesAsync(cancellationToken)).Contains(item.Sha256))
			{
				throw new PdfInboxDuplicateException("Diese PDF ist bereits einem anderen NovaNein-Beleg zugeordnet.");
			}
			SapDocumentIdentity identity = new SapDocumentIdentity(direction, docEntry, expectedDocNum, kind.ToDomain());
			DocumentRecord document;
			if ((uint)(kind - 2) <= 1u)
			{
				document = await documents.CreateAsync(identity, item.Sha256, item.OriginalFileName, actor, cancellationToken);
				document = (await documents.RecordValidationAsync(document.Id, new ValidationResult(ReviewSignal.Yellow, new[] { "Gutschrift ist archiviert; der DATEV-Transfer bleibt bis zur eigenen fachlichen Abnahme gesperrt." }), "credit-note-gate", cancellationToken)) ?? throw new InvalidOperationException("Die Gutschrift konnte nicht als manuell zu prüfen gespeichert werden.");
			}
			else
			{
				DocumentRecord documentRecord = ((direction != DocumentDirection.Incoming) ? (await outgoing.AcceptAsync(identity, item.Sha256, item.OriginalFileName, actor, cancellationToken)) : (await incoming.AcceptAsync(identity, item.Sha256, item.OriginalFileName, actor, cancellationToken)));
				document = documentRecord;
			}
			PdfInboxItem assigned = await inbox.AssignAsync(inboxId, document.Sap, document.Id, actor, cancellationToken);
			if ((object)assigned == null)
			{
				throw new InvalidOperationException("Die PDF-Zuordnung konnte nicht gespeichert werden.");
			}
			result = new PdfInboxAssignmentResult(assigned, document);
		}
		return result;
	}

	public Task<bool> RejectAsync(Guid inboxId, string reason, CancellationToken cancellationToken = default(CancellationToken))
	{
		return inbox.RejectAsync(inboxId, reason, cancellationToken);
	}

	public Task<IReadOnlySet<string>> ListPdfHashesAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return inbox.ListPdfHashesAsync(cancellationToken);
	}

	private static decimal Score(PdfInboxItem inboxItem, SapDocumentSnapshot snapshot, out IReadOnlyList<string> reasons)
	{
		List<string> details = new List<string>();
		decimal score = default(decimal);
		if (!string.IsNullOrWhiteSpace(inboxItem.InvoiceNumber) && (Normalize(inboxItem.InvoiceNumber) == Normalize(snapshot.InvoiceNumber) || Normalize(inboxItem.InvoiceNumber) == snapshot.DocNum.ToString(CultureInfo.InvariantCulture)))
		{
			score += 0.60m;
			details.Add("Rechnungsnummer stimmt überein.");
		}
		decimal? grossAmount = inboxItem.GrossAmount;
		if (grossAmount.HasValue)
		{
			decimal amount = grossAmount.GetValueOrDefault();
			if (Math.Abs(amount - snapshot.GrossAmount) <= 0.01m)
			{
				score += 0.20m;
				details.Add("Bruttobetrag stimmt überein.");
			}
		}
		DateOnly? invoiceDate = inboxItem.InvoiceDate;
		if (invoiceDate.HasValue && Math.Abs(invoiceDate.GetValueOrDefault().DayNumber - snapshot.DocumentDate.DayNumber) <= 3)
		{
			score += 0.10m;
			details.Add("Belegdatum liegt nahe beieinander.");
		}
		if (!string.IsNullOrWhiteSpace(inboxItem.BusinessPartner) && SimilarPartner(inboxItem.BusinessPartner, snapshot.BusinessPartnerName))
		{
			score += 0.10m;
			details.Add("Geschäftspartner passt.");
		}
		if (!string.IsNullOrWhiteSpace(inboxItem.Currency) && string.Equals(inboxItem.Currency, snapshot.Currency, StringComparison.OrdinalIgnoreCase))
		{
			score += 0.02m;
			details.Add("Währung stimmt überein.");
		}
		reasons = details;
		return score;
	}

	private static bool SimilarPartner(string left, string right)
	{
		string normalizedLeft = NormalizeWords(left);
		string normalizedRight = NormalizeWords(right);
		if (normalizedLeft.Length > 3 && normalizedRight.Length > 3)
		{
			if (!normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase))
			{
				return normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
		return false;
	}

	private static string NormalizeWords(string value)
	{
		return string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
	}

	private static string Normalize(string value)
	{
		return string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();
	}

	private static DocumentDirection DirectionFor(SapDocumentKind kind)
	{
		if ((kind != SapDocumentKind.PurchaseInvoice && kind != SapDocumentKind.PurchaseCreditNote) || 1 == 0)
		{
			return DocumentDirection.Outgoing;
		}
		return DocumentDirection.Incoming;
	}

	private async Task<SapDocumentSnapshot?> TryGetSnapshotAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken)
	{
		try
		{
			return await sap.GetDocumentAsync(kind, docEntry, cancellationToken);
		}
		catch (Exception ex) when (((Func<bool>)delegate
		{
			// Could not convert BlockContainer to single expression
			bool flag = !(ex is TaskCanceledException) || !cancellationToken.IsCancellationRequested;
			if (flag)
			{
				flag = ((ex is HttpRequestException || ex is InvalidOperationException || ex is TaskCanceledException || ex is SqlException || ex is KeyNotFoundException || ex is NotSupportedException) ? true : false);
			}
			return flag;
		}).Invoke())
		{
			return null;
		}
	}
}
