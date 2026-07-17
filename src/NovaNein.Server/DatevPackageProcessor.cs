using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NovaNein.Datev;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class DatevPackageProcessor(DocumentStore documents, TransferEvidenceStore evidence, DatevTransferRequestStore transferRequests, ISapServiceLayerClient sap, IConfiguration configuration, ILogger<DatevPackageProcessor> logger)
{
	internal const string DefaultSupplierZip = "12345";
	private readonly DatevPackageGenerator _generator = new DatevPackageGenerator();

	public async Task ProcessAsync(DocumentJob job, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (job.Kind != DocumentJobKind.CreateDatevPackage)
		{
			throw new ArgumentException("Falscher Jobtyp für das DATEV-Paket.", "job");
		}
		DocumentRecord document = (await documents.GetAsync(job.DocumentId, cancellationToken)) ?? throw new InvalidOperationException("Beleg zum DATEV-Job fehlt.");
		if (!DocumentWorkflow.MayCreateDatevPackage(document) && document.Status != DocumentStatus.Packaged)
		{
			throw new InvalidOperationException("Das DATEV-Paket darf erst nach der fachlichen Freigabe vorbereitet werden.");
		}
		if (document.Sap.Type.IsCreditNote() && !await documents.HasCreditNoteDatevReleaseAsync(document.Id, cancellationToken))
		{
			throw new InvalidDataException("Die Gutschrift wurde noch nicht ausdrücklich für DATEV freigegeben.");
		}
		TransferEvidence existing = await evidence.GetAsync(document.Id, cancellationToken);
		if ((object)existing != null)
		{
			if (document.Status != DocumentStatus.Packaged)
			{
				var synchronized = await documents.RecordDatevPackagePreparedAsync(document.Id, existing.PackageFileName, existing.PackageSha256, existing.PackagePreparedAt ?? DateTimeOffset.UtcNow, "datev-package-worker", cancellationToken);
				if (synchronized is null) throw new InvalidOperationException("Der vorhandene DATEV-Paketnachweis konnte nicht mit dem Belegstatus synchronisiert werden.");
			}
			bool flag = ShouldAutoTransfer(document, existing.PackagePreparedAt);
			if (flag)
			{
				flag = (object)(await transferRequests.GetByDocumentAsync(document.Id, cancellationToken)) == null;
			}
			if (flag)
			{
				await transferRequests.RequestAsync(document.Id, existing.PackageSha256, TransferActor(document), cancellationToken);
			}
			return;
		}
		string pdfRoot = Path.GetFullPath(configuration["Storage:DocumentRoot"] ?? "data/documents");
		string pdfPath = Path.Combine(pdfRoot, document.PdfSha256 + ".pdf");
		if (!File.Exists(pdfPath))
		{
			throw new FileNotFoundException("Die archivierte PDF für das DATEV-Paket fehlt.", pdfPath);
		}
		SapDocumentKind kind = document.Sap.Type.ToServer(document.Sap.Direction);
		SapDocumentSnapshot snapshot = await sap.GetDocumentAsync(kind, document.Sap.DocEntry, cancellationToken);
		if (snapshot.DocNum != document.Sap.DocNum)
		{
			throw new InvalidDataException("SAP-DocNum und gespeicherte Belegnummer stimmen nicht überein.");
		}
		string packageRoot = configuration["Datev:PackageDirectory"]
			?? throw new InvalidOperationException("Datev:PackageDirectory fehlt; Pakete dürfen nicht direkt im DATEV-Watchfolder erzeugt werden.");
		EnsurePackageDirectoryOutsideWatchfolders(packageRoot);
		string packageDirectory = DirectionDirectory(packageRoot, document.Sap.Direction);
		SapAccountingDocument accounting = (await sap.GetAccountingDocumentAsync(kind, document.Sap.DocEntry, cancellationToken)) ?? throw new InvalidDataException("Gesperrt – SAP-Buchungsdaten fehlen. Es wurden keine DATEV-Werte aus PDF oder Konfiguration ergänzt.");
		DatevIncomingInvoice invoice = BuildInvoice(accounting, document.Sap.Direction);
		string description = document.Sap.Type.IsCreditNote()
			? (document.Sap.Direction == DocumentDirection.Incoming ? "Eingangsgutschrift" : "Ausgangsgutschrift")
			: (document.Sap.Direction == DocumentDirection.Incoming ? "Eingangsrechnung" : "Ausgangsrechnung");
		string clientNumber = configuration["Datev:ClientNumber"] ?? string.Empty;
		if (string.IsNullOrWhiteSpace(clientNumber)) throw new InvalidOperationException("Datev:ClientNumber fehlt.");
		string clientName = configuration["Datev:ClientName"] ?? invoice.ClientName;
		if (string.IsNullOrWhiteSpace(clientName)) throw new InvalidOperationException("Datev:ClientName fehlt und konnte nicht aus SAP ermittelt werden.");
		DatevDocumentManifest manifest = new DatevDocumentManifest(document.Sap.Direction, snapshot.DocNum, DateTimeOffset.UtcNow, clientNumber, clientName, description);
		string documentXml = DatevDocumentXmlGenerator.Create(manifest);
		string invoiceXml = ((document.Sap.Direction == DocumentDirection.Incoming) ? DatevInvoiceXmlGenerator.CreateIncoming(invoice) : DatevInvoiceXmlGenerator.CreateOutgoing(invoice));
		ValidateConfiguredXsds(documentXml, invoiceXml);
		DatevPackageGenerator generator = _generator;
		DocumentDirection direction = document.Sap.Direction;
		int docNum = snapshot.DocNum;
		string documentXml2 = documentXml;
		string invoiceXml2 = invoiceXml;
		CreatedDatevPackage created = generator.Create(new DatevPackageRequest(direction, docNum, documentXml2, invoiceXml2, await File.ReadAllBytesAsync(pdfPath, cancellationToken)), packageDirectory);
		DateTimeOffset preparedAt = DateTimeOffset.UtcNow;
		await evidence.RegisterPackageAsync(document.Id, created.Sha256, Path.GetFileName(created.Path), preparedAt, cancellationToken);
		if (await documents.RecordDatevPackagePreparedAsync(document.Id, created.Path, created.Sha256, preparedAt, "datev-package-worker", cancellationToken) is null)
		{
			throw new InvalidOperationException("Der DATEV-Paketstatus konnte nicht atomar gespeichert werden.");
		}
		bool flag2 = ShouldAutoTransfer(document, preparedAt);
		if (flag2)
		{
			flag2 = (object)(await transferRequests.GetByDocumentAsync(document.Id, cancellationToken)) == null;
		}
		if (flag2)
		{
			await transferRequests.RequestAsync(document.Id, created.Sha256, TransferActor(document), cancellationToken);
		}
		if (configuration.GetValue("Datev:AllowDirectTransfer", defaultValue: false))
		{
			string target = DirectionDirectory(configuration[(document.Sap.Direction == DocumentDirection.Incoming) ? "Datev:IncomingFolder" : "Datev:OutgoingFolder"], document.Sap.Direction);
			if (!string.Equals(Path.GetFullPath(created.Path), Path.GetFullPath(Path.Combine(target, Path.GetFileName(created.Path))), StringComparison.OrdinalIgnoreCase))
			{
				new AtomicWatchfolderTransfer().MoveCompletedPackage(created.Path, target, Path.GetFileName(created.Path));
			}
			logger.LogInformation("DATEV-Paket {Package} wurde für den Transfer bereitgestellt.", Path.GetFileName(created.Path));
		}
	}

	private bool ShouldAutoTransfer(DocumentRecord document, DateTimeOffset? packagePreparedAt)
	{
		bool autoTransferApprovedInvoices = configuration.GetValue("Datev:AutoTransferApprovedInvoices", defaultValue: false);
		bool autoTransferGreenOnly = configuration.GetValue("Datev:AutoTransferGreenOnly", defaultValue: false);
		bool explicitlyReleasedCreditNote = document.Sap.Type.IsCreditNote();
		if (!packagePreparedAt.HasValue
			|| (!explicitlyReleasedCreditNote && (!autoTransferApprovedInvoices && (!autoTransferGreenOnly || document.Signal != ReviewSignal.Green))))
			return false;
		if (!DateTimeOffset.TryParse(configuration["Datev:AutoTransferNotBeforeUtc"], out var notBefore))
		{
			logger.LogWarning("Der automatische DATEV-Transfer ist aktiviert, aber Datev:AutoTransferNotBeforeUtc fehlt; Pakete werden sicher nicht automatisch eingereiht.");
			return false;
		}
		return packagePreparedAt.Value >= notBefore;
	}

	private static string TransferActor(DocumentRecord document) =>
		document.Sap.Type.IsCreditNote() ? "approved-credit-note-workflow" : "approved-invoice-workflow";

	private DatevIncomingInvoice BuildInvoice(SapAccountingDocument accounting, DocumentDirection direction)
	{
		SapDocumentSnapshot snapshot = accounting.Snapshot;
		bool creditNote = snapshot.Kind.IsCreditNote();
		if (!accounting.IsComplete)
		{
			throw new InvalidDataException("Gesperrt – " + string.Join(" ", accounting.CompletenessIssues));
		}
		if (accounting.DatevMappings.Count != (from x in accounting.Taxes
			select x.TaxCode into x
			where !string.IsNullOrWhiteSpace(x)
			select x).Distinct<string>(StringComparer.OrdinalIgnoreCase).Count())
		{
			throw new InvalidDataException("Gesperrt – für mindestens ein SAP-Steuerkennzeichen fehlt eine freigegebene AVT1/DATEV-Zuordnung.");
		}
		string partner = (string.IsNullOrWhiteSpace(snapshot.BusinessPartnerName) ? snapshot.BusinessPartnerCode : snapshot.BusinessPartnerName);
		if (snapshot.DocNum <= 0 || string.IsNullOrWhiteSpace(snapshot.InvoiceNumber) || string.IsNullOrWhiteSpace(partner))
		{
			throw new InvalidDataException("SAP-Belegnummer, Rechnungsnummer und Geschäftspartner müssen für DATEV vorhanden sein.");
		}
		if (snapshot.GrossAmount <= 0m || snapshot.DocumentDate == DateOnly.MinValue)
		{
			throw new InvalidDataException("SAP-Bruttobetrag und Belegdatum müssen für DATEV gültig sein.");
		}
		string currency = snapshot.Currency?.Trim().ToUpperInvariant();
		if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3 || currency.Any((char ch) => !char.IsLetter(ch)))
		{
			throw new InvalidDataException("Die SAP-Währung fehlt; das DATEV-Paket wurde gesperrt.");
		}
		decimal gross = decimal.Round(snapshot.GrossAmount, 2, MidpointRounding.AwayFromZero);
		decimal net = decimal.Round(accounting.Lines.Sum((SapDocumentLine x) => x.NetAmount), 2, MidpointRounding.AwayFromZero);
		EffectiveAccountingTotals totals = CalculateEffectiveAccountingTotals(accounting, creditNote, gross);
		decimal tax = totals.EffectiveTax;
		decimal taxBreakdownTotal = totals.EffectiveTaxBreakdown;
		if (Math.Abs(tax - taxBreakdownTotal) > 0.01m)
		{
			throw new InvalidDataException("SAP-Steuerzeilen und SAP-Positionssteuern widersprechen sich.");
		}
		decimal journalDebit = totals.JournalDebit;
		decimal journalCredit = totals.JournalCredit;
		bool hasForeignCurrencyJournal = accounting.JournalLines.Any(line => !string.IsNullOrWhiteSpace(line.Currency));
		decimal expectedJournalTotal = decimal.Round(gross + totals.ReverseChargeTax, 2, MidpointRounding.AwayFromZero);
		if (Math.Abs(journalDebit - journalCredit) > 0.01m || (!hasForeignCurrencyJournal && Math.Abs(journalDebit - expectedJournalTotal) > 0.01m))
		{
			throw new InvalidDataException("SAP-Journal ist nicht ausgeglichen oder stimmt nicht mit dem SAP-Brutto überein.");
		}
		decimal taxRate = ((net == 0m) ? 0m : decimal.Round(tax / net * 100m, 4, MidpointRounding.AwayFromZero));
		if (Math.Abs(net + tax - gross) > 0.01m)
		{
			throw new InvalidDataException("SAP-Netto, SAP-Steuer und SAP-Brutto stimmen nicht innerhalb der DATEV-Rundungsgrenze überein.");
		}
		if (taxRate < 0m || taxRate > 1000m)
		{
			throw new InvalidDataException("Der aus SAP-Steuerzeilen ermittelte Steuersatz ist ungültig.");
		}
		DatevBookingLine[] bookingLines = accounting.Lines
			.Where(line => line.NetAmount != 0m || line.TaxAmount != 0m)
		.Select(delegate(SapDocumentLine line)
		{
			DatevBookingMapping datevBookingMapping = accounting.DatevMappings.FirstOrDefault((DatevBookingMapping m) => string.Equals(m.SapTaxCode, line.TaxCode, StringComparison.OrdinalIgnoreCase));
			if ((object)datevBookingMapping == null || string.IsNullOrWhiteSpace(line.Account) || string.IsNullOrWhiteSpace(datevBookingMapping.DatevBuCode))
			{
				throw new InvalidDataException($"Gesperrt – SAP-Position {line.LineNum} besitzt keine vollständige DATEV-Zuordnung.");
			}
			if (!string.IsNullOrWhiteSpace(datevBookingMapping.DatevAccount) && !string.Equals(datevBookingMapping.DatevAccount, line.Account, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException($"Gesperrt – die freigegebene DATEV-Kontonummer stimmt bei SAP-Position {line.LineNum} nicht mit dem SAP-Konto überein.");
			}
			if (!line.Account.All(char.IsDigit) || !datevBookingMapping.DatevBuCode.All(char.IsDigit))
			{
				throw new InvalidDataException("SAP-Konto und AVT1-BU-Schlüssel dürfen nur Ziffern enthalten.");
			}
			bool isReverseCharge = IsReverseChargeDocument(accounting, gross, creditNote)
				|| line.IsReverseCharge
				|| accounting.Taxes.Any(taxLine => taxLine.IsReverseCharge && string.Equals(taxLine.TaxCode, line.TaxCode, StringComparison.OrdinalIgnoreCase));
			decimal effectiveTax = isReverseCharge ? 0m : line.TaxAmount;
			decimal grossAmount = decimal.Round(line.NetAmount + effectiveTax, 2, MidpointRounding.AwayFromZero);
			string text = ResolveDebitCredit(accounting.JournalLines, line.Account, direction, creditNote);
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new InvalidDataException("Gesperrt – für SAP-Konto " + line.Account + " fehlt die Soll/Haben-Zuordnung im Journal.");
			}
			return new DatevBookingLine(line.Account, datevBookingMapping.DatevBuCode, line.NetAmount, grossAmount, effectiveTax, line.TaxRate, currency, string.IsNullOrWhiteSpace(line.Description) ? "SAP-Position" : line.Description, (line.Quantity <= 0m) ? 1m : line.Quantity, text);
		}).ToArray();
		if (bookingLines.Length == 0)
		{
			throw new InvalidDataException("SAP-Beleg enthält keine betragswirksame DATEV-Position.");
		}
		string account = bookingLines.Select((DatevBookingLine x) => x.AccountNumber).FirstOrDefault() ?? throw new InvalidDataException("SAP-Konto fehlt.");
		string buCode = bookingLines.Select((DatevBookingLine x) => x.BuCode).FirstOrDefault() ?? throw new InvalidDataException("AVT1-BU-Schlüssel fehlt.");
		string partnerAccount = accounting.PartnerAccountNumber ?? throw new InvalidDataException("SAP-Geschäftspartnerkonto fehlt.");
		if (!partnerAccount.All(char.IsDigit) || !uint.TryParse(partnerAccount, out var numericPartnerAccount) || numericPartnerAccount < 10000)
			throw new InvalidDataException("Das SAP-Geschäftspartnerkonto ist keine gültige DATEV-Personenkontonummer.");
		string bookingText = creditNote
			? (direction == DocumentDirection.Incoming ? "Eingangsgutschrift" : "Ausgangsgutschrift")
			: (direction == DocumentDirection.Incoming ? "Eingangsrechnung" : "Ausgangsrechnung");
		string invoiceId = DatevInvoiceXmlGenerator.NormalizeInvoiceId(snapshot.InvoiceNumber);
		if (!string.Equals(invoiceId, snapshot.InvoiceNumber, StringComparison.Ordinal))
		{
			logger.LogWarning("SAP-Rechnungsnummer {InvoiceNumber} enthält für DATEV unzulässige Zeichen; DATEV verwendet {DatevInvoiceId} für Beleg {DocNum}.", snapshot.InvoiceNumber, invoiceId, snapshot.DocNum);
		}
		string supplierZip = ResolveSupplierZip(accounting.PartnerZip);
		if (!string.Equals(supplierZip, accounting.PartnerZip?.Trim(), StringComparison.Ordinal))
		{
			logger.LogWarning("SAP-Lieferanten-PLZ fehlt für Beleg {DocNum}; DATEV verwendet den Ersatzwert {SupplierZip}.", snapshot.DocNum, supplierZip);
		}
		return new DatevIncomingInvoice(snapshot.DocNum, invoiceId, snapshot.DocumentDate, partner, accounting.PartnerVatId ?? "", accounting.PartnerStreet ?? "", supplierZip, accounting.PartnerCity ?? "", "DE", bookingText, net, gross, tax, taxRate, currency, account, buCode, accounting.CompanyVatId ?? "", "", accounting.CompanyStreet ?? "", accounting.CompanyZip ?? "", accounting.CompanyCity ?? "", "DE", "", "", partnerAccount, bookingText, 1m, bookingLines, accounting.CompanyName ?? throw new InvalidDataException("SAP-Firmenname fehlt."), creditNote ? "Gutschrift/Rechnungskorrektur" : "Rechnung");
	}

	internal static string ResolveSupplierZip(string? supplierZip) =>
		string.IsNullOrWhiteSpace(supplierZip) ? DefaultSupplierZip : supplierZip.Trim();

	internal static EffectiveAccountingTotals CalculateEffectiveAccountingTotals(SapAccountingDocument accounting, bool creditNote, decimal gross)
	{
		ArgumentNullException.ThrowIfNull(accounting);
		bool inferredReverseCharge = IsReverseChargeDocument(accounting, gross, creditNote);
		var reverseCodes = accounting.Taxes
			.Where(tax => tax.IsReverseCharge && !string.IsNullOrWhiteSpace(tax.TaxCode))
			.Select(tax => tax.TaxCode.Trim())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (inferredReverseCharge)
		{
			foreach (var taxCode in accounting.Taxes.Where(tax => !string.IsNullOrWhiteSpace(tax.TaxCode)).Select(tax => tax.TaxCode.Trim())) reverseCodes.Add(taxCode);
		}
		bool IsReverseCharge(SapDocumentLine line) => line.IsReverseCharge || reverseCodes.Contains(line.TaxCode?.Trim() ?? string.Empty);
		decimal effectiveTax = decimal.Round(accounting.Lines.Where(line => !IsReverseCharge(line)).Sum(line => line.TaxAmount), 2, MidpointRounding.AwayFromZero);
		decimal effectiveTaxBreakdown = decimal.Round(accounting.Taxes.Where(tax => !tax.IsReverseCharge && !reverseCodes.Contains(tax.TaxCode?.Trim() ?? string.Empty)).Sum(tax => tax.TaxAmount), 2, MidpointRounding.AwayFromZero);
		decimal reverseChargeTax = decimal.Round(accounting.Taxes.Where(tax => tax.IsReverseCharge || reverseCodes.Contains(tax.TaxCode?.Trim() ?? string.Empty)).Sum(tax => Math.Abs(tax.ReverseChargeTaxAmount != 0m ? tax.ReverseChargeTaxAmount : tax.TaxAmount)), 2, MidpointRounding.AwayFromZero);
		if (reverseChargeTax == 0m)
		{
			reverseChargeTax = decimal.Round(accounting.Lines.Where(IsReverseCharge).Sum(line => Math.Abs(line.TaxAmount)), 2, MidpointRounding.AwayFromZero);
		}
		decimal journalDebit = decimal.Round(accounting.JournalLines.Sum(line => line.Debit), 2, MidpointRounding.AwayFromZero);
		decimal journalCredit = decimal.Round(accounting.JournalLines.Sum(line => line.Credit), 2, MidpointRounding.AwayFromZero);
		if (creditNote)
		{
			journalDebit = Math.Abs(journalDebit);
			journalCredit = Math.Abs(journalCredit);
		}
		return new EffectiveAccountingTotals(effectiveTax, effectiveTaxBreakdown, reverseChargeTax, journalDebit, journalCredit);
	}

	internal static bool IsReverseChargeDocument(SapAccountingDocument accounting, decimal gross, bool creditNote)
	{
		ArgumentNullException.ThrowIfNull(accounting);
		if (accounting.Taxes.Any(tax => tax.IsReverseCharge) || accounting.Lines.Any(line => line.IsReverseCharge)) return true;
		decimal net = decimal.Round(accounting.Lines.Sum(line => line.NetAmount), 2, MidpointRounding.AwayFromZero);
		decimal tax = decimal.Round(accounting.Lines.Sum(line => Math.Abs(line.TaxAmount)), 2, MidpointRounding.AwayFromZero);
		if (tax <= 0m || Math.Abs(net - gross) > 0.01m) return false;
		decimal debit = decimal.Round(accounting.JournalLines.Sum(line => line.Debit), 2, MidpointRounding.AwayFromZero);
		decimal credit = decimal.Round(accounting.JournalLines.Sum(line => line.Credit), 2, MidpointRounding.AwayFromZero);
		if (creditNote)
		{
			debit = Math.Abs(debit);
			credit = Math.Abs(credit);
		}
		return Math.Abs(debit - credit) <= 0.01m && Math.Abs(debit - (gross + tax)) <= 0.01m;
	}

	internal static string? ResolveDebitCredit(IReadOnlyList<SapJournalLine> journalLines, string account, DocumentDirection direction, bool creditNote = false)
	{
		ArgumentNullException.ThrowIfNull(journalLines);
		var exact = journalLines.FirstOrDefault(line =>
			string.Equals(line.Account, account, StringComparison.OrdinalIgnoreCase)
			&& (!string.IsNullOrWhiteSpace(line.DebitCredit) || line.Debit != 0m || line.Credit != 0m));
		if (exact is not null)
		{
			if (!string.IsNullOrWhiteSpace(exact.DebitCredit)) return exact.DebitCredit;
			if (exact.Debit > 0m || exact.Credit < 0m) return "S";
			if (exact.Credit > 0m || exact.Debit < 0m) return "H";
		}

		// SAP may post item accounts through a determination/collective account so
		// that the document line account is not present verbatim in JDT1. The side
		// of an invoice is nevertheless deterministic: expense lines are debit on
		// incoming invoices, revenue lines credit on outgoing invoices. Only use
		// that fallback when the balanced journal actually contains that side.
		string expected = direction == DocumentDirection.Incoming
			? (creditNote ? "H" : "S")
			: (creditNote ? "S" : "H");
		return journalLines.Any(line => EffectiveDebitCredit(line) == expected)
			? expected
			: null;
	}

	private static string? EffectiveDebitCredit(SapJournalLine line)
	{
		if (!string.IsNullOrWhiteSpace(line.DebitCredit)) return line.DebitCredit;
		if (line.Debit > 0m || line.Credit < 0m) return "S";
		if (line.Credit > 0m || line.Debit < 0m) return "H";
		return null;
	}

	private void ValidateConfiguredXsds(string documentXml, string invoiceXml)
	{
		if (configuration.GetValue("Datev:RequireXsdValidation", defaultValue: true))
		{
			string[] paths = configuration.GetSection("Datev:XsdPaths").Get<string[]>() ?? Array.Empty<string>();
			DatevPackageGenerator.ValidateAgainstLocalXsds(documentXml, paths);
			DatevPackageGenerator.ValidateAgainstLocalXsds(invoiceXml, paths);
		}
	}

	private static string DirectionDirectory(string? root, DocumentDirection direction)
	{
		if (string.IsNullOrWhiteSpace(root))
		{
			throw new InvalidOperationException("Das DATEV-Verzeichnis fehlt.");
		}
		string full = Path.GetFullPath(root);
		string directionFolder = ((direction == DocumentDirection.Incoming) ? "Rechnungseingang" : "Rechnungsausgang");
		return Path.Combine(full, directionFolder);
	}

	private void EnsurePackageDirectoryOutsideWatchfolders(string packageRoot)
	{
		var package = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
		foreach (var key in new[] { "Datev:WatchFolder", "Datev:IncomingFolder", "Datev:OutgoingFolder" })
		{
			var configuredWatchfolder = configuration[key];
			if (string.IsNullOrWhiteSpace(configuredWatchfolder)) continue;
			var watchfolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredWatchfolder));
			var separator = Path.DirectorySeparatorChar.ToString();
			var overlaps = string.Equals(package, watchfolder, StringComparison.OrdinalIgnoreCase)
				|| package.StartsWith(watchfolder + separator, StringComparison.OrdinalIgnoreCase)
				|| watchfolder.StartsWith(package + separator, StringComparison.OrdinalIgnoreCase);
			if (overlaps)
			{
				throw new InvalidOperationException("Datev:PackageDirectory muss vollständig außerhalb aller DATEV-Watchfolder liegen.");
			}
		}
	}
}

internal sealed record EffectiveAccountingTotals(decimal EffectiveTax, decimal EffectiveTaxBreakdown, decimal ReverseChargeTax, decimal JournalDebit, decimal JournalCredit);
