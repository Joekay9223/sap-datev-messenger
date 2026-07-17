using System.Globalization;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class InvoiceProposalService(
	AutomaticBookingStore store,
	ISapServiceLayerClient sap,
	IPdfInvoiceTextExtractor extractor,
	DocumentStore documents,
	DocumentJobQueue jobs,
	IGmailApiClient gmail,
	IConfiguration configuration,
	ILogger<InvoiceProposalService> logger)
{
	private const decimal AmountTolerance = 0.02m;

	public async Task<InvoiceProposal> CreateOrRecalculateAsync(
		MailSourceRecord mail,
		MailAttachmentRecord attachment,
		Guid? existingProposalId,
		CancellationToken cancellationToken = default)
	{
		if (!string.Equals(Path.GetExtension(attachment.LocalPath), ".pdf", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Nur PDF-Anhänge werden als Buchungsvorschlag interpretiert.");

		var extractedFacts = extractor.Extract(attachment.LocalPath);
		var facts = InventoryGoodsClassifier.Apply(extractedFacts);
		var direction = DetectDirection(facts);
		var findings = new List<string>();
		var supplierName = direction == "incoming"
			? FirstNotEmpty(facts.IssuerName, facts.BusinessPartnerName)
			: FirstNotEmpty(facts.RecipientName, facts.BusinessPartnerName);
		var supplierVatId = direction == "incoming" ? facts.IssuerVatId ?? facts.VatId : facts.RecipientVatId ?? facts.VatId;
		string? supplierCode = null;
		SupplierProposal? supplierProposal = null;

		if (direction == "incoming")
		{
			var matches = await sap.FindSuppliersAsync(
				supplierName,
				supplierVatId,
				facts.SupplierTaxNumber,
				facts.SupplierIban,
				facts.SupplierStreet,
				facts.SupplierPostalCode,
				facts.SupplierCity,
				cancellationToken);
			var qualified = matches.Where(candidate => candidate.MatchScore >= 45m).ToArray();
			if (qualified.Length == 1)
				supplierCode = qualified[0].CardCode;
			else if (qualified.Length > 1)
				findings.Add("Mehrere SAP-Lieferanten passen zum Beleg; die Buchung ist gesperrt.");
			else
			{
				findings.Add("Kein eindeutiger SAP-Lieferant gefunden; getrennte Stammdatenfreigabe erforderlich.");
				var proposalId = existingProposalId ?? Guid.NewGuid();
				supplierProposal = new SupplierProposal(
					Guid.NewGuid(),
					proposalId,
					1,
					"Proposed",
					BuildCardCode(proposalId),
					supplierName,
					supplierVatId,
					facts.SupplierTaxNumber,
					facts.SupplierIban,
					facts.SupplierStreet,
					facts.SupplierPostalCode,
					facts.SupplierCity,
					"DE",
					DateTimeOffset.UtcNow,
					DateTimeOffset.UtcNow,
					null,
					null,
					null,
					null);
			}
		}

		IReadOnlyList<SapCodingCandidate> codingHistory = string.IsNullOrWhiteSpace(supplierCode)
			? []
			: await sap.GetSupplierCodingHistoryAsync(supplierCode, cancellationToken);
		SapAccountingDocument? outgoingAccounting = null;
		if (direction == "outgoing")
		{
			if (!TryParseSapDocNum(facts.InvoiceNumber, out var docNum)
				|| await sap.FindDocumentByDocNumAsync(SapDocumentKind.Invoice, docNum, cancellationToken) is not { } snapshot)
			{
				findings.Add("Keine eindeutige vorhandene SAP-Ausgangsrechnung gefunden; aus Gmail wird niemals neu angelegt.");
			}
			else
			{
				outgoingAccounting = await sap.GetAccountingDocumentAsync(SapDocumentKind.Invoice, snapshot.DocEntry, cancellationToken);
				AddOutgoingSapFindings(facts, snapshot, outgoingAccounting, findings);
			}
		}
		var lines = outgoingAccounting?.IsComplete == true
			? BuildOutgoingLines(outgoingAccounting)
			: BuildLines(facts, codingHistory, findings);
		var netAmount = facts.NetAmount ?? lines.Sum(line => line.NetAmount);
		var taxAmount = facts.TaxAmount ?? lines.Sum(line => line.TaxAmount);
		var grossAmount = facts.GrossAmount;

		if (Math.Abs(netAmount + taxAmount - grossAmount) > AmountTolerance)
			findings.Add($"Netto plus Steuer ({netAmount + taxAmount:0.00}) stimmt nicht mit Brutto ({grossAmount:0.00}) überein.");
		if (Math.Abs(lines.Sum(line => line.NetAmount) - netAmount) > AmountTolerance)
			findings.Add("Die Summe der Buchungszeilen stimmt nicht mit der Rechnungsnettosumme überein.");
		if (direction == "incoming" && (facts.HasGoodsCharacteristics || lines.Any(line => line.LooksLikeGoods)))
			findings.Add("Bestandsgeführte Ware erkannt (Rohstoff oder Verpackungsmaterial); artikelgenaue SAP-Buchung ist erforderlich.");
		else if (direction == "incoming" && (extractedFacts.HasGoodsCharacteristics || extractedFacts.Lines?.Any(line => line.LooksLikeGoods) == true))
			findings.Add("Physische Position erkannt, aber keine bestandsgeführte Ware; Verarbeitung als einfache Kostenrechnung ist zulässig.");
		if (direction == "incoming" && facts.HasPurchaseOrderReference)
			findings.Add("Bestell- oder Wareneingangsbezug für bestandsgeführte Ware erkannt; automatische Kostenbuchung ist gesperrt.");
		if (facts.DocumentType == "credit_note") findings.Add("Gutschrift erkannt; automatische SAP-Buchung ist im ersten Umfang gesperrt.");
		if (direction == "incoming" && facts.IsReverseCharge) findings.Add("Reverse-Charge-Fall erkannt; automatische SAP-Buchung ist gesperrt.");
		if (direction == "incoming" && !string.Equals(facts.Currency, "EUR", StringComparison.OrdinalIgnoreCase)) findings.Add("Fremdwährung erkannt; automatische SAP-Buchung ist gesperrt.");
		if (!facts.IsInvoice) findings.Add("Das Dokument wurde nicht sicher als Rechnung erkannt.");
		if (facts.HasRequiredFieldConflicts) findings.Add("Widersprüchliche Pflichtfelder im Beleg.");
		if (facts.IsDocumentQualityUncertain || !facts.HasReadableDocumentContent) findings.Add("Dokumentqualität oder Lesbarkeit ist unsicher.");
		if (string.IsNullOrWhiteSpace(facts.InvoiceNumber)) findings.Add("Rechnungsnummer fehlt.");
		if (grossAmount <= 0m) findings.Add("Der Rechnungsbetrag ist für eine Kostenrechnung ungültig.");
		if (await store.HasPotentialDuplicateAsync(supplierCode, facts.InvoiceNumber, grossAmount, existingProposalId, cancellationToken))
			findings.Add("Mögliche NovaNein-Dublette mit gleicher Rechnungsnummer und gleichem Betrag.");
		if (direction == "incoming"
			&& !string.IsNullOrWhiteSpace(supplierCode)
			&& !string.IsNullOrWhiteSpace(facts.InvoiceNumber)
			&& await sap.FindPurchaseInvoiceDuplicateAsync(supplierCode, facts.InvoiceNumber, cancellationToken) is { } duplicate)
			findings.Add($"SAP-Dublette: Eingangsrechnung ist bereits als Beleg {duplicate.DocNum} (DocEntry {duplicate.DocEntry}) vorhanden.");

		var hardBlocked = direction == "unknown"
			|| direction == "incoming" && facts.HasPurchaseOrderReference
			|| direction == "incoming" && facts.HasGoodsCharacteristics
			|| facts.DocumentType == "credit_note"
			|| direction == "incoming" && facts.IsReverseCharge
			|| direction == "incoming" && !string.Equals(facts.Currency, "EUR", StringComparison.OrdinalIgnoreCase)
			|| !facts.IsInvoice
			|| grossAmount <= 0m
			|| Math.Abs(netAmount + taxAmount - grossAmount) > AmountTolerance
			|| findings.Any(finding => finding.Contains("Mehrere SAP-Lieferanten", StringComparison.Ordinal)
				|| finding.Contains("Keine eindeutige vorhandene SAP-Ausgangsrechnung", StringComparison.Ordinal)
				|| finding.Contains("SAP-Ausgangsrechnung stimmt nicht", StringComparison.Ordinal)
				|| finding.Contains("SAP-Buchungsdaten der Ausgangsrechnung", StringComparison.Ordinal)
				|| finding.Contains("Dublette", StringComparison.Ordinal));
		var needsReview = !hardBlocked && (
			string.IsNullOrWhiteSpace(supplierCode) && direction == "incoming"
			|| lines.Any(line => string.IsNullOrWhiteSpace(line.Account) || string.IsNullOrWhiteSpace(line.TaxCode))
			|| facts.HasRequiredFieldConflicts
			|| facts.IsDocumentQualityUncertain);
		var status = hardBlocked ? MailSourceStatuses.Blocked : needsReview ? MailSourceStatuses.NeedsReview : MailSourceStatuses.ProposalReady;
		var signal = hardBlocked ? "red" : needsReview ? "yellow" : "green";
		var now = DateTimeOffset.UtcNow;
		var id = existingProposalId ?? supplierProposal?.InvoiceProposalId ?? Guid.NewGuid();
		var proposal = new InvoiceProposal(
			id,
			mail.Id,
			attachment.Id,
			1,
			direction,
			status,
			signal,
			facts.DocumentType,
			facts.InvoiceNumber,
			supplierName,
			supplierCode,
			supplierVatId,
			facts.SupplierTaxNumber,
			facts.SupplierIban,
			facts.InvoiceDate,
			facts.ServiceDate,
			facts.DueDate,
			netAmount,
			taxAmount,
			grossAmount,
			facts.Currency,
			facts.HasPurchaseOrderReference,
			facts.HasGoodsCharacteristics,
			facts.IsReverseCharge,
			BuildReason(direction, supplierCode, codingHistory, facts.CodingReason),
			attachment.Sha256,
			now,
			now,
			null,
			null,
			null,
			findings,
			lines);
		return await store.SaveProposalAsync(proposal, supplierProposal, cancellationToken);
	}

	public async Task<InvoiceProposal> RecalculateAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var current = await store.GetProposalAsync(id, cancellationToken)
			?? throw new KeyNotFoundException("Der Buchungsvorschlag wurde nicht gefunden.");
		var attachment = current.MailSource?.Attachments?.SingleOrDefault(item => item.Id == current.MailAttachmentId)
			?? throw new InvalidOperationException("Der Gmail-PDF-Anhang zum Vorschlag fehlt.");
		return await CreateOrRecalculateAsync(current.MailSource!, attachment, id, cancellationToken);
	}

	public async Task<InvoiceProposal> ReclassifyStoredInventoryAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var current = await store.GetProposalAsync(id, cancellationToken)
			?? throw new KeyNotFoundException("Der Buchungsvorschlag wurde nicht gefunden.");
		return await store.SaveProposalAsync(ReclassifyStoredInventoryProposal(current), null, cancellationToken);
	}

	internal static InvoiceProposal ReclassifyStoredInventoryProposal(InvoiceProposal current)
	{
		if (current.Status is MailSourceStatuses.Approved
			or MailSourceStatuses.SapPosting
			or MailSourceStatuses.SapReadbackConfirmed
			or MailSourceStatuses.DatevPrepared
			or MailSourceStatuses.DatevFinalized)
			throw new InvalidOperationException("Ein bereits gebuchter oder an DATEV übergebener Vorschlag darf nicht umklassifiziert werden.");

		var lines = current.Lines
			.Select(line => line with { LooksLikeGoods = InventoryGoodsClassifier.RequiresItemPosting(line.Description) })
			.ToArray();
		if (!lines.Any(line => line.LooksLikeGoods))
			throw new InvalidOperationException("In den gespeicherten Rechnungszeilen wurde keine bestandsgeführte Ware erkannt.");

		var findings = current.Findings
			.Where(finding => !finding.StartsWith("Physische Position erkannt, aber keine bestandsgeführte Ware", StringComparison.Ordinal)
				&& !finding.StartsWith("Bestandsgeführte Ware erkannt", StringComparison.Ordinal))
			.Append("Bestandsgeführte Ware erkannt (Rohstoff oder Verpackungsmaterial); artikelgenaue SAP-Buchung ist erforderlich.")
			.ToArray();
		return current with
		{
			Status = MailSourceStatuses.Blocked,
			Signal = "red",
			HasGoodsCharacteristics = true,
			Findings = findings,
			Lines = lines
		};
	}

	public Task<InvoiceProposal> RejectAsync(Guid id, InvoiceProposalDecisionRequest request, string actor, CancellationToken cancellationToken = default)
		=> store.RejectProposalAsync(id, request.ExpectedVersion, request.Reason, actor, cancellationToken);

	public async Task<InvoiceProposal> ApproveAndPostAsync(
		Guid id,
		InvoiceProposalDecisionRequest request,
		string actor,
		CancellationToken cancellationToken = default)
	{
		var proposal = await store.BeginPostingAsync(id, request.ExpectedVersion, request.Reason, request.Lines, actor, cancellationToken);
		try
		{
			if (proposal.Signal == "red") throw new InvalidOperationException("Rote Vorschläge dürfen nicht gebucht werden.");
			if (proposal.Direction == "outgoing")
				return await LinkOutgoingAsync(proposal, actor, cancellationToken);
			if (proposal.Direction != "incoming") throw new InvalidOperationException("Die Belegseite ist nicht eindeutig.");
			if (string.IsNullOrWhiteSpace(proposal.SupplierCode)) throw new InvalidOperationException("Der SAP-Lieferant fehlt.");
			var liveSuppliers = (await sap.FindSuppliersAsync(
				proposal.SupplierName,
				proposal.SupplierVatId,
				proposal.SupplierTaxNumber,
				proposal.SupplierIban,
				null,
				null,
				null,
				cancellationToken))
				.Where(candidate => candidate.MatchScore >= 45m)
				.ToArray();
			if (liveSuppliers.Length != 1
				|| !string.Equals(liveSuppliers[0].CardCode, proposal.SupplierCode, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Der Lieferantenabgleich ist beim Live-Readback nicht mehr eindeutig.");
			ValidateTotals(proposal);
			foreach (var line in proposal.Lines)
			{
				var account = await sap.ValidateAccountAsync(line.Account, cancellationToken);
				if (!account.Exists || !account.Active)
					throw new InvalidOperationException($"Sachkonto {line.Account} ist in SAP nicht aktiv verfügbar.");
				if (account.RequiresDimensions)
					throw new InvalidOperationException($"Sachkonto {line.Account} verlangt Dimensionen und ist für die Automatik gesperrt.");
				var taxCode = await sap.ValidateTaxCodeAsync(line.TaxCode, cancellationToken);
				if (!taxCode.Exists || !taxCode.Active)
					throw new InvalidOperationException($"SAP-Steuerschlüssel {line.TaxCode} in Zeile {line.LineNumber} ist nicht aktiv verfügbar.");
			}
			if (await sap.FindPurchaseInvoiceDuplicateAsync(proposal.SupplierCode, proposal.InvoiceNumber, cancellationToken) != null)
				throw new InvalidOperationException("SAP enthält bereits eine aktive Eingangsrechnung mit diesem Lieferanten und dieser Rechnungsnummer.");
			var attachment = proposal.MailSource?.Attachments?.Single(item => item.Id == proposal.MailAttachmentId)
				?? throw new InvalidOperationException("Der originale Gmail-PDF-Anhang fehlt.");
			var posting = await sap.CreatePurchaseInvoiceAsync(new SapPurchaseInvoiceRequest(
				proposal.Id,
				proposal.SourceSha256,
				proposal.SupplierCode,
				proposal.InvoiceNumber,
				proposal.InvoiceDate,
				proposal.ServiceDate,
				proposal.DueDate,
				proposal.Currency,
				proposal.GrossAmount,
				attachment.LocalPath,
				proposal.Lines.Select(line => new SapPurchaseInvoiceLineRequest(
					line.LineNumber, line.Description, line.NetAmount, line.Account, line.TaxCode)).ToArray()),
				actor,
				cancellationToken);
			await ConfirmAccountingReadbackAsync(proposal, posting, cancellationToken);
			await store.RecordPostingAsync(posting with { PostedBy = actor }, cancellationToken);
			await LinkDocumentPipelineAsync(proposal, posting, actor, cancellationToken);
			await MarkGmailBookedAsync(proposal, cancellationToken);
			return (await store.GetProposalAsync(id, cancellationToken))!;
		}
		catch (Exception exception)
		{
			if (exception is SapOrphanAttachmentException orphan)
				await store.RecordOrphanAttachmentAsync(id, orphan.AttachmentEntry, orphan.Message, cancellationToken);
			await store.MarkPostingFailedAsync(id, exception.Message, actor, cancellationToken);
			logger.LogError(exception, "Buchungsvorschlag {ProposalId} konnte nicht sicher verarbeitet werden.", id);
			throw;
		}
	}

	public async Task<SupplierProposal> ApproveAndCreateSupplierAsync(
		Guid id,
		SupplierProposalApprovalRequest request,
		string actor,
		CancellationToken cancellationToken = default)
	{
		var current = await store.GetSupplierProposalAsync(id, cancellationToken)
			?? throw new KeyNotFoundException("Der Lieferantenvorschlag wurde nicht gefunden.");
		var cardCode = string.IsNullOrWhiteSpace(request.CardCode) ? current.ProposedCardCode : request.CardCode.Trim();
		var creating = await store.BeginSupplierCreationAsync(id, request.ExpectedVersion, cardCode, request.Reason, actor, cancellationToken);
		try
		{
			var created = await sap.CreateSupplierAsync(new SapSupplierCreateRequest(
				creating.ProposedCardCode,
				creating.Name,
				creating.VatId,
				creating.TaxNumber,
				creating.Iban,
				creating.Street,
				creating.PostalCode,
				creating.City,
				creating.CountryCode,
				creating.InvoiceProposalId.ToString()),
				cancellationToken);
			await store.CompleteSupplierCreationAsync(id, created, actor, cancellationToken);
			return (await store.GetSupplierProposalAsync(id, cancellationToken))!;
		}
		catch (Exception exception)
		{
			await store.FailSupplierCreationAsync(id, exception.Message, cancellationToken);
			throw;
		}
	}

	private async Task<InvoiceProposal> LinkOutgoingAsync(InvoiceProposal proposal, string actor, CancellationToken cancellationToken)
	{
		if (!TryParseSapDocNum(proposal.InvoiceNumber, out var docNum))
			throw new InvalidOperationException("Die SAP-Ausgangsrechnungsnummer ist nicht eindeutig.");
		var snapshot = await sap.FindDocumentByDocNumAsync(SapDocumentKind.Invoice, docNum, cancellationToken)
			?? throw new InvalidOperationException("Die vorhandene SAP-Ausgangsrechnung wurde nicht gefunden.");
		var existing = await documents.GetBySapAsync(DocumentDirection.Outgoing, SapBusinessDocumentType.Invoice, snapshot.DocEntry, cancellationToken);
		if (existing != null && !string.Equals(existing.PdfSha256, proposal.SourceSha256, StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Im Beleg-Cockpit ist bereits eine andere PDF mit dieser SAP-Ausgangsrechnung verknüpft.");

		// Der vollständige PDF/SAP-Abgleich muss vor dem ersten Schreibzugriff bestehen.
		// So kann ein falsch erkannter Beleg niemals zunächst an SAP angehängt und erst
		// anschließend wegen einer fachlichen Abweichung abgelehnt werden.
		var accounting = await ConfirmAccountingReadbackAsync(proposal, new SapPostingResult(
			proposal.Id,
			snapshot.DocEntry,
			snapshot.DocNum,
			snapshot.TransId ?? 0,
			snapshot.AttachmentEntry ?? 0,
			string.Empty,
			DateTimeOffset.UtcNow,
			actor), cancellationToken);

		var attachment = proposal.MailSource?.Attachments?.Single(item => item.Id == proposal.MailAttachmentId)
			?? throw new InvalidOperationException("Der originale Gmail-PDF-Anhang fehlt.");
		await sap.AttachPdfAsync(SapDocumentKind.Invoice, snapshot.DocEntry, snapshot.DocNum, attachment.LocalPath, cancellationToken);
		snapshot = await sap.GetDocumentAsync(SapDocumentKind.Invoice, snapshot.DocEntry, cancellationToken);
		if (!snapshot.AttachmentEntry.HasValue)
			throw new InvalidOperationException("SAP hat die PDF-Verknüpfung der Ausgangsrechnung nicht bestätigt.");
		var result = new SapPostingResult(
			proposal.Id,
			snapshot.DocEntry,
			snapshot.DocNum,
			accounting.TransId ?? 0,
			snapshot.AttachmentEntry ?? 0,
			accounting.SourceHash,
			DateTimeOffset.UtcNow,
			actor);
		await store.RecordPostingAsync(result, cancellationToken);
		await LinkDocumentPipelineAsync(proposal, result, actor, cancellationToken);
		await MarkGmailBookedAsync(proposal, cancellationToken);
		return (await store.GetProposalAsync(proposal.Id, cancellationToken))!;
	}

	private async Task<SapAccountingDocument> ConfirmAccountingReadbackAsync(InvoiceProposal proposal, SapPostingResult posting, CancellationToken cancellationToken)
	{
		var kind = proposal.Direction == "incoming" ? SapDocumentKind.PurchaseInvoice : SapDocumentKind.Invoice;
		for (var attempt = 0; attempt < 9; attempt++)
		{
			var accounting = await sap.GetAccountingDocumentAsync(kind, posting.DocEntry, cancellationToken);
			if (accounting?.IsComplete == true)
			{
				if (accounting.Snapshot.DocNum != posting.DocNum) throw new InvalidOperationException("SAP-DocNum änderte sich beim Readback.");
				if (Math.Abs(accounting.Snapshot.GrossAmount - proposal.GrossAmount) > AmountTolerance)
					throw new InvalidOperationException("SAP-Bruttobetrag stimmt nicht mit dem freigegebenen Vorschlag überein.");
				if (proposal.Direction == "incoming"
					&& !string.Equals(accounting.Snapshot.BusinessPartnerCode, proposal.SupplierCode, StringComparison.OrdinalIgnoreCase))
					throw new InvalidOperationException("SAP-Lieferant stimmt nicht mit dem freigegebenen Vorschlag überein.");
				if (accounting.Lines.Count != proposal.Lines.Count)
					throw new InvalidOperationException("Die Anzahl der SAP-Kontierungszeilen stimmt nicht mit dem freigegebenen Vorschlag überein.");
				foreach (var proposalLine in proposal.Lines)
				{
					var sapLine = accounting.Lines.SingleOrDefault(line => line.LineNum == proposalLine.LineNumber - 1)
						?? accounting.Lines.SingleOrDefault(line => line.LineNum == proposalLine.LineNumber);
					if (sapLine == null)
						throw new InvalidOperationException($"SAP-Kontierungszeile {proposalLine.LineNumber} fehlt im Readback.");
					if (!string.Equals(sapLine.Account, proposalLine.Account, StringComparison.OrdinalIgnoreCase)
						|| !string.Equals(sapLine.TaxCode, proposalLine.TaxCode, StringComparison.OrdinalIgnoreCase)
						|| Math.Abs(sapLine.NetAmount - proposalLine.NetAmount) > AmountTolerance)
						throw new InvalidOperationException($"SAP-Kontierungszeile {proposalLine.LineNumber} weicht vom freigegebenen Vorschlag ab.");
				}
				return accounting;
			}
			await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(1.8, attempt)), cancellationToken);
		}
		throw new InvalidOperationException("SAP-Journal-, Steuer-, Kontierungs- oder AVT1-Daten sind nach dem Backoff noch nicht vollständig sichtbar.");
	}

	private async Task LinkDocumentPipelineAsync(InvoiceProposal proposal, SapPostingResult posting, string actor, CancellationToken cancellationToken)
	{
		var direction = proposal.Direction == "incoming" ? DocumentDirection.Incoming : DocumentDirection.Outgoing;
		var type = proposal.Direction == "incoming" ? SapBusinessDocumentType.PurchaseInvoice : SapBusinessDocumentType.Invoice;
		var document = await documents.GetBySapAsync(direction, type, posting.DocEntry, cancellationToken);
		if (document != null && !string.Equals(document.PdfSha256, proposal.SourceSha256, StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Im Beleg-Cockpit ist bereits eine andere PDF mit diesem SAP-Beleg verknüpft.");
		if (document == null)
		{
			document = await documents.CreateAsync(
				new SapDocumentIdentity(direction, posting.DocEntry, posting.DocNum, type),
				proposal.SourceSha256,
				proposal.MailSource?.Attachments?.Single(item => item.Id == proposal.MailAttachmentId).FileName ?? $"Rechnung-{posting.DocNum}.pdf",
				actor,
				cancellationToken);
			document = await documents.RecordValidationAsync(document.Id, new ValidationResult(ReviewSignal.Green, ["SAP-Buchung und vollständiger Readback wurden bestätigt."]), actor, cancellationToken)
				?? throw new InvalidOperationException("Die Dokumentvalidierung konnte nach SAP-Readback nicht gespeichert werden.");
		}
		document = await documents.MarkAttachedToSapAsync(document.Id, actor, cancellationToken) ?? document;
		if (configuration.GetValue("Datev:AutoPreparePackages", false))
			await jobs.EnsureEnqueuedAsync(document.Id, DocumentJobKind.CreateDatevPackage, cancellationToken);
	}

	private async Task MarkGmailBookedAsync(InvoiceProposal proposal, CancellationToken cancellationToken)
	{
		if (proposal.MailSource == null || !gmail.IsConfigured) return;
		if (!await store.AreAllMailProposalsResolvedAsync(proposal.MailSourceId, cancellationToken)) return;
		var labels = await gmail.EnsureLabelsAsync(cancellationToken);
		await gmail.ModifyLabelsAsync(
			proposal.MailSource.GmailMessageId,
			[labels["NovaNein/Gebucht"]],
			[labels["NovaNein/Prüfung"], labels["NovaNein/Fehler"]],
			cancellationToken);
	}

	internal static IReadOnlyList<InvoiceProposalLine> BuildLines(
		ExtractedInvoiceFacts facts,
		IReadOnlyList<SapCodingCandidate> history,
		ICollection<string> findings)
	{
		var sourceLines = facts.Lines?.Count > 0
			? facts.Lines
			: [new ExtractedInvoiceLine(1, FirstNotEmpty(facts.BusinessPartnerName, "Kostenrechnung"), facts.NetAmount ?? facts.GrossAmount - (facts.TaxAmount ?? 0m), facts.TaxAmount ?? 0m, facts.TaxRate, facts.SuggestedAccount, facts.SuggestedTaxCode, facts.HasGoodsCharacteristics)];
		var historical = history.FirstOrDefault();
		var result = new List<InvoiceProposalLine>();
		foreach (var line in sourceLines)
		{
			var account = FirstNotEmpty(historical?.Account, line.SuggestedAccount);
			var taxCode = FirstNotEmpty(historical?.TaxCode, line.SuggestedTaxCode);
			var source = historical != null && string.Equals(account, historical.Account, StringComparison.OrdinalIgnoreCase)
				? "SAP-Historie"
				: !string.IsNullOrWhiteSpace(line.SuggestedAccount) ? "KI-Belegvorschlag" : "offen";
			if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(taxCode))
				findings.Add($"Kontierung in Zeile {line.LineNumber} ist unvollständig.");
			result.Add(new InvoiceProposalLine(
				line.LineNumber,
				FirstNotEmpty(line.Description, "Kostenposition"),
				line.NetAmount,
				line.TaxAmount,
				line.TaxRate,
				account,
				taxCode,
				source,
				source == "SAP-Historie" ? historical!.Confidence : source == "KI-Belegvorschlag" ? 0.65m : 0m,
				line.LooksLikeGoods));
		}
		return result;
	}

	internal static IReadOnlyList<InvoiceProposalLine> BuildOutgoingLines(SapAccountingDocument accounting)
	{
		ArgumentNullException.ThrowIfNull(accounting);
		return accounting.Lines
			.OrderBy(line => line.LineNum)
			.Select(line => new InvoiceProposalLine(
				line.LineNum + 1,
				FirstNotEmpty(line.Description, "SAP-Ausgangsposition"),
				line.NetAmount,
				line.TaxAmount,
				line.TaxRate,
				line.Account,
				line.TaxCode,
				"SAP-Readback",
				1m,
				false))
			.ToArray();
	}

	internal static void AddOutgoingSapFindings(
		ExtractedInvoiceFacts facts,
		SapDocumentSnapshot snapshot,
		SapAccountingDocument? accounting,
		ICollection<string> findings)
	{
		ArgumentNullException.ThrowIfNull(facts);
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(findings);
		if (accounting == null)
		{
			findings.Add("SAP-Buchungsdaten der Ausgangsrechnung fehlen; automatische Verarbeitung ist gesperrt.");
			return;
		}
		if (!accounting.IsComplete)
		{
			findings.Add("SAP-Buchungsdaten der Ausgangsrechnung sind unvollständig: " + string.Join(" ", accounting.CompletenessIssues));
			return;
		}
		if (Math.Abs(snapshot.GrossAmount - facts.GrossAmount) > AmountTolerance)
			findings.Add("SAP-Ausgangsrechnung stimmt nicht mit dem PDF-Bruttobetrag überein.");
		if (facts.NetAmount.HasValue && Math.Abs(accounting.Lines.Sum(line => line.NetAmount) - facts.NetAmount.Value) > AmountTolerance)
			findings.Add("SAP-Ausgangsrechnung stimmt nicht mit dem PDF-Nettobetrag überein.");
		if (facts.TaxAmount.HasValue && Math.Abs(accounting.Lines.Sum(line => line.TaxAmount) - facts.TaxAmount.Value) > AmountTolerance)
			findings.Add("SAP-Ausgangsrechnung stimmt nicht mit dem PDF-Steuerbetrag überein.");
		if (!string.Equals(snapshot.Currency, facts.Currency, StringComparison.OrdinalIgnoreCase))
			findings.Add("SAP-Ausgangsrechnung stimmt nicht mit der PDF-Währung überein.");
		if (snapshot.DocumentDate != facts.InvoiceDate)
			findings.Add("SAP-Ausgangsrechnung stimmt nicht mit dem PDF-Rechnungsdatum überein.");
		var pdfPartner = Normalize(FirstNotEmpty(facts.RecipientName, facts.BusinessPartnerName));
		var sapPartner = Normalize(snapshot.BusinessPartnerName);
		if (pdfPartner.Length > 0 && sapPartner.Length > 0
			&& !pdfPartner.Contains(sapPartner, StringComparison.Ordinal)
			&& !sapPartner.Contains(pdfPartner, StringComparison.Ordinal))
			findings.Add("SAP-Ausgangsrechnung stimmt nicht mit dem PDF-Kunden überein.");
	}

	private string DetectDirection(ExtractedInvoiceFacts facts)
	{
		var companyVat = Normalize(configuration["Company:VatId"] ?? "DE000000000");
		var names = configuration.GetSection("Company:Names").Get<string[]>() ?? ["demo-company"];
		var issuerIsCompany = Normalize(facts.IssuerVatId) == companyVat || names.Any(name => ContainsNormalized(facts.IssuerName, name));
		var recipientIsCompany = Normalize(facts.RecipientVatId) == companyVat || names.Any(name => ContainsNormalized(facts.RecipientName, name));
		if (recipientIsCompany && !issuerIsCompany) return "incoming";
		if (issuerIsCompany && !recipientIsCompany) return "outgoing";
		return "unknown";
	}

	private static void ValidateTotals(InvoiceProposal proposal)
	{
		if (proposal.Lines.Count == 0) throw new InvalidOperationException("Buchungszeilen fehlen.");
		if (Math.Abs(proposal.NetAmount + proposal.TaxAmount - proposal.GrossAmount) > AmountTolerance)
			throw new InvalidOperationException("Netto plus Steuer stimmt nicht mit Brutto überein.");
		if (Math.Abs(proposal.Lines.Sum(line => line.NetAmount) - proposal.NetAmount) > AmountTolerance)
			throw new InvalidOperationException("Buchungszeilen stimmen nicht mit der Nettosumme überein.");
	}

	private static string BuildReason(string direction, string? supplierCode, IReadOnlyList<SapCodingCandidate> history, string? aiReason)
	{
		var parts = new List<string> { direction == "incoming" ? "Eingangsrechnung erkannt." : direction == "outgoing" ? "Ausgangsrechnung erkannt." : "Belegseite unklar." };
		if (!string.IsNullOrWhiteSpace(supplierCode)) parts.Add($"SAP-Partner {supplierCode} eindeutig zugeordnet.");
		if (history.Count > 0) parts.Add("Kontierung aus freigegebener SAP-Historie priorisiert.");
		if (!string.IsNullOrWhiteSpace(aiReason)) parts.Add(aiReason.Trim());
		return string.Join(" ", parts);
	}

	private static string BuildCardCode(Guid proposalId)
		=> "LNN" + proposalId.ToString("N")[..9].ToUpperInvariant();

	private static bool TryParseSapDocNum(string invoiceNumber, out int docNum)
	{
		var digits = new string(invoiceNumber.Where(char.IsDigit).ToArray());
		return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out docNum) && docNum > 0;
	}

	private static string FirstNotEmpty(params string?[] values)
		=> values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

	private static string Normalize(string? value)
		=> string.IsNullOrWhiteSpace(value) ? string.Empty : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

	private static bool ContainsNormalized(string? value, string expected)
		=> Normalize(value).Contains(Normalize(expected), StringComparison.Ordinal);
}
