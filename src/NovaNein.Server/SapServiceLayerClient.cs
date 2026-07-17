using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class SapServiceLayerClient(HttpClient httpClient, IConfiguration configuration) : ISapServiceLayerClient
{
	private static readonly JsonSerializerOptions SapJsonOptions = new()
	{
		PropertyNamingPolicy = null,
		DictionaryKeyPolicy = null
	};

	private readonly SemaphoreSlim _loginLock = new SemaphoreSlim(1, 1);

	private string? _sessionId;

	public async Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (docEntry <= 0)
		{
			throw new ArgumentOutOfRangeException("docEntry");
		}
		string entity = EntityName(kind);
		string select = "$select=DocEntry,DocNum,CardCode,CardName,NumAtCard,DocDate,DocTotal,DocCurrency,AttachmentEntry,TransNum,Comments";
		using HttpResponseMessage response = await SendGetAsync($"{entity}({docEntry})?{select}", cancellationToken);
		response.EnsureSuccessStatusCode();
		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		return ReadSnapshot(json.RootElement, kind);
	}

	public async Task<IReadOnlyList<SapDocumentSnapshot>> ListDocumentsAsync(SapDocumentKind kind, DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (toEntryDate < fromEntryDate)
		{
			throw new ArgumentException("Der Endtag darf nicht vor dem Starttag liegen.");
		}
		string entity = EntityName(kind);
		string select = "$select=DocEntry,DocNum,CardCode,CardName,NumAtCard,DocDate,DocTotal,DocCurrency,AttachmentEntry,CreationDate,Comments";
		string filter = $"CreationDate ge '{fromEntryDate:yyyy-MM-dd}' and CreationDate le '{toEntryDate:yyyy-MM-dd}'";
		string nextPage = $"{entity}?{select}&$filter={Uri.EscapeDataString(filter)}&$top=100";
		List<SapDocumentSnapshot> result = new List<SapDocumentSnapshot>();
		while (!string.IsNullOrWhiteSpace(nextPage))
		{
			using HttpResponseMessage response = await SendGetAsync(nextPage, cancellationToken);
			response.EnsureSuccessStatusCode();
			using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
			foreach (JsonElement item in json.RootElement.GetProperty("value").EnumerateArray())
			{
				result.Add(ReadSnapshot(item, kind));
			}
			nextPage = ((json.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String) ? link.GetString() : null);
		}
		return (from x in result
			orderby x.EntryDate ?? x.DocumentDate, x.DocNum
			select x).ToArray();
	}

	public async Task<SapDocumentSnapshot?> FindDocumentByDocNumAsync(SapDocumentKind kind, int docNum, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (docNum <= 0)
		{
			throw new ArgumentOutOfRangeException("docNum");
		}
		string entity = EntityName(kind);
		using HttpResponseMessage response = await SendGetAsync($"{entity}?$filter=DocNum%20eq%20{docNum}&$top=2", cancellationToken);
		response.EnsureSuccessStatusCode();
		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		JsonElement values = json.RootElement.GetProperty("value");
		if (values.GetArrayLength() == 0)
		{
			return null;
		}
		return await GetDocumentAsync(kind, values[0].GetProperty("DocEntry").GetInt32(), cancellationToken);
	}

	public async Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (toEntryDate < fromEntryDate)
		{
			throw new ArgumentException("Der Endtag darf nicht vor dem Starttag liegen.");
		}
		List<SapAttachmentGap> gaps = new List<SapAttachmentGap>();
		SapDocumentKind[] values = Enum.GetValues<SapDocumentKind>();
		foreach (SapDocumentKind kind in values)
		{
			string entity = EntityName(kind);
			string filter = $"CreationDate ge '{fromEntryDate:yyyy-MM-dd}' and CreationDate le '{toEntryDate:yyyy-MM-dd}'";
			string nextPage = entity + "?$select=DocEntry,DocNum,CreationDate,AttachmentEntry&$filter=" + Uri.EscapeDataString(filter) + "&$top=100";
			while (!string.IsNullOrWhiteSpace(nextPage))
			{
				using HttpResponseMessage response = await SendGetAsync(nextPage, cancellationToken);
				response.EnsureSuccessStatusCode();
				using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
				foreach (JsonElement item in json.RootElement.GetProperty("value").EnumerateArray())
				{
					JsonElement attachment;
					int? attachmentEntry = ((item.TryGetProperty("AttachmentEntry", out attachment) && attachment.ValueKind != JsonValueKind.Null) ? new int?(attachment.GetInt32()) : ((int?)null));
					bool hasValue = attachmentEntry.HasValue;
					bool flag = hasValue;
					if (flag)
					{
						flag = await AttachmentContainsPdfAsync(attachmentEntry.Value, cancellationToken);
					}
					if (!flag)
					{
						gaps.Add(new SapAttachmentGap(kind, item.GetProperty("DocEntry").GetInt32(), item.GetProperty("DocNum").GetInt32(), ParseServiceLayerDate(item.GetProperty("CreationDate"), "SAP CreationDate fehlt."), attachmentEntry));
					}
				}
				nextPage = ((json.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String) ? link.GetString() : null);
			}
		}
		return (from x in gaps
			orderby x.EntryDate, x.Kind, x.DocNum
			select x).ToArray();
	}

	public async Task CheckReadinessAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		using HttpResponseMessage response = await SendGetAsync("Invoices?$select=DocEntry&$top=1", cancellationToken);
		response.EnsureSuccessStatusCode();
	}

	public Task AttachPdfAsync(SapDocumentKind kind, int docEntry, int expectedDocNum, string localPdfPath, CancellationToken cancellationToken = default(CancellationToken))
	{
		return AttachPdfCoreAsync(kind, docEntry, expectedDocNum, localPdfPath, cancellationToken);
	}

	private async Task AttachPdfCoreAsync(SapDocumentKind kind, int docEntry, int expectedDocNum, string localPdfPath, CancellationToken cancellationToken)
	{
		if (!CanWriteAttachments())
		{
			throw new InvalidOperationException("SAP-Anhangsschreiben ist deaktiviert. Es erfordert Sap:Mode=write-enabled und Sap:EnableAttachments2Writes=true.");
		}
		if (docEntry <= 0)
		{
			throw new ArgumentOutOfRangeException("docEntry");
		}
		if (expectedDocNum <= 0)
		{
			throw new ArgumentOutOfRangeException("expectedDocNum");
		}
		string fullPath = ValidateAttachmentSource(localPdfPath);
		string fileName = Path.GetFileNameWithoutExtension(fullPath);
		SapDocumentSnapshot current = await GetDocumentAsync(kind, docEntry, cancellationToken);
		if (current.DocNum != expectedDocNum)
		{
			throw new InvalidOperationException($"SAP-DocNum {current.DocNum} stimmt nicht mit der gespeicherten NovaNein-DocNum {expectedDocNum} überein. Es wurde nichts angehängt.");
		}
		if (current.AttachmentEntry.HasValue)
		{
			if (await AttachmentContainsFileAsync(current.AttachmentEntry.Value, fileName, "pdf", cancellationToken))
			{
				return;
			}
			throw new InvalidOperationException($"SAP-Beleg {current.DocNum} besitzt bereits den AttachmentEntry {current.AttachmentEntry}. NovaNein überschreibt bestehende SAP-Anhänge nicht.");
		}
		string sourcePath = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("Der PDF-Quellordner fehlt.");
		var payload = new
		{
			Attachments2_Lines = new[]
			{
				new
				{
					SourcePath = sourcePath,
					FileName = fileName,
					FileExtension = "pdf",
					AttachmentDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
					Override = "tNO",
					FreeText = "NovaNein: " + fileName
				}
			}
		};
		using HttpResponseMessage create = await SendJsonAsync(HttpMethod.Post, "Attachments2", payload, cancellationToken);
		create.EnsureSuccessStatusCode();
		using JsonDocument created = JsonDocument.Parse(await create.Content.ReadAsStreamAsync(cancellationToken));
		int attachmentEntry = created.RootElement.GetProperty("AbsoluteEntry").GetInt32();
		string entity = EntityName(kind);
		using HttpResponseMessage link = await SendJsonAsync(HttpMethod.Patch, $"{entity}({docEntry})", new
		{
			AttachmentEntry = attachmentEntry
		}, cancellationToken);
		link.EnsureSuccessStatusCode();
		using HttpResponseMessage verification = await SendGetAsync($"{entity}({docEntry})?$select=AttachmentEntry", cancellationToken);
		verification.EnsureSuccessStatusCode();
		using JsonDocument verifiedDocument = JsonDocument.Parse(await verification.Content.ReadAsStreamAsync(cancellationToken));
		if (!verifiedDocument.RootElement.TryGetProperty("AttachmentEntry", out var verifiedEntry) || verifiedEntry.ValueKind == JsonValueKind.Null || verifiedEntry.GetInt32() != attachmentEntry)
		{
			throw new InvalidOperationException($"SAP hat die neue AttachmentEntry-Verknüpfung {attachmentEntry} nicht bestätigt.");
		}
		using HttpResponseMessage attachment = await SendGetAsync($"Attachments2({attachmentEntry})", cancellationToken);
		attachment.EnsureSuccessStatusCode();
		using JsonDocument verifiedAttachment = JsonDocument.Parse(await attachment.Content.ReadAsStreamAsync(cancellationToken));
		if (!ContainsAttachmentFile(verifiedAttachment.RootElement, fileName, "pdf"))
		{
			throw new InvalidOperationException("SAP hat die PDF-Anlage " + fileName + ".pdf nicht bestätigt.");
		}
	}

	public async Task<IReadOnlyList<SapSupplierCandidate>> FindSuppliersAsync(
		string name,
		string? vatId,
		string? taxNumber,
		string? iban,
		string? street,
		string? postalCode,
		string? city,
		CancellationToken cancellationToken = default)
	{
		var filter = Uri.EscapeDataString("CardType eq 'cSupplier' and Valid eq 'tYES'");
		using var response = await SendGetAsync(
			"BusinessPartners?$select=CardCode,CardName,FederalTaxID,AdditionalID&$expand=BPAddresses,BPBankAccounts&$filter=" + filter + "&$top=1000",
			cancellationToken);
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		var candidates = new List<SapSupplierCandidate>();
		foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
		{
			var cardCode = GetString(item, "CardCode");
			var cardName = GetString(item, "CardName");
			var candidateVat = GetString(item, "FederalTaxID");
			var candidateTax = GetString(item, "AdditionalID");
			var candidateIban = FirstString(item, "BPBankAccounts", "IBAN");
			var candidateStreet = FirstString(item, "BPAddresses", "Street");
			var candidateZip = FirstString(item, "BPAddresses", "ZipCode");
			var candidateCity = FirstString(item, "BPAddresses", "City");
			var reasons = new List<string>();
			decimal score = 0m;
			AddExactScore(vatId, candidateVat, 100m, "USt-ID", reasons, ref score);
			AddExactScore(taxNumber, candidateTax, 90m, "Steuernummer", reasons, ref score);
			AddExactScore(iban, candidateIban, 85m, "IBAN", reasons, ref score);
			AddExactScore(name, cardName, 45m, "Name", reasons, ref score);
			AddExactScore(postalCode, candidateZip, 20m, "PLZ", reasons, ref score);
			AddExactScore(city, candidateCity, 15m, "Ort", reasons, ref score);
			AddExactScore(street, candidateStreet, 10m, "Straße", reasons, ref score);
			if (!string.IsNullOrWhiteSpace(cardCode) && score > 0m)
			{
				candidates.Add(new SapSupplierCandidate(
					cardCode, cardName, candidateVat, candidateTax, candidateIban,
					candidateStreet, candidateZip, candidateCity, score, reasons));
			}
		}
		return candidates.OrderByDescending(candidate => candidate.MatchScore).ThenBy(candidate => candidate.CardCode).ToArray();
	}

	public async Task<IReadOnlyList<SapCodingCandidate>> GetSupplierCodingHistoryAsync(string cardCode, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cardCode);
		var escapedCardCode = cardCode.Replace("'", "''", StringComparison.Ordinal);
		var filter = Uri.EscapeDataString($"CardCode eq '{escapedCardCode}' and Cancelled eq 'tNO'");
		using var response = await SendGetAsync(
			"PurchaseInvoices?$select=DocEntry&$expand=DocumentLines($select=AccountCode,TaxCode,ItemDescription)&$filter=" + filter + "&$orderby=DocDate desc&$top=50",
			cancellationToken);
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		var groups = new Dictionary<(string Account, string TaxCode, string Description), int>();
		foreach (var invoice in json.RootElement.GetProperty("value").EnumerateArray())
		{
			if (!invoice.TryGetProperty("DocumentLines", out var lines) || lines.ValueKind != JsonValueKind.Array) continue;
			foreach (var line in lines.EnumerateArray())
			{
				var account = GetString(line, "AccountCode");
				var taxCode = GetString(line, "TaxCode");
				var description = GetString(line, "ItemDescription");
				if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(taxCode)) continue;
				var key = (account, taxCode, description);
				groups[key] = groups.GetValueOrDefault(key) + 1;
			}
		}
		return groups
			.OrderByDescending(item => item.Value)
			.Select(item => new SapCodingCandidate(
				item.Key.Account,
				item.Key.TaxCode,
				item.Key.Description,
				item.Value,
				Math.Min(0.98m, 0.60m + item.Value * 0.05m),
				"SAP-Historie"))
			.ToArray();
	}

	public async Task<SapAccountValidation> ValidateAccountAsync(string account, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(account))
			return new SapAccountValidation(account, false, false, false, null, "Sachkonto fehlt.");
		var escaped = account.Trim().Replace("'", "''", StringComparison.Ordinal);
		using var response = await SendGetAsync($"ChartOfAccounts('{escaped}')?$select=Code,Name,ActiveAccount", cancellationToken);
		if (response.StatusCode == HttpStatusCode.NotFound)
			return new SapAccountValidation(account, false, false, false, null, "Sachkonto existiert nicht.");
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		var activeValue = GetString(json.RootElement, "ActiveAccount");
		var active = string.Equals(activeValue, "tYES", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(activeValue, "Y", StringComparison.OrdinalIgnoreCase);
		var dimensions = configuration.GetSection("Sap:AccountsRequiringDimensions").Get<string[]>() ?? [];
		var requiresDimensions = dimensions.Contains(account.Trim(), StringComparer.OrdinalIgnoreCase);
		return new SapAccountValidation(account.Trim(), true, active, requiresDimensions, GetString(json.RootElement, "Name"), null);
	}

	public async Task<SapTaxCodeValidation> ValidateTaxCodeAsync(string taxCode, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(taxCode))
			return new SapTaxCodeValidation(taxCode, false, false, null, "Steuerschlüssel fehlt.");
		var entity = configuration["Sap:TaxCodeEntity"] ?? "VatGroups";
		var escaped = taxCode.Trim().Replace("'", "''", StringComparison.Ordinal);
		using var response = await SendGetAsync($"{entity}('{escaped}')?$select=Code,Name,Inactive", cancellationToken);
		if (response.StatusCode == HttpStatusCode.NotFound)
			return new SapTaxCodeValidation(taxCode, false, false, null, "Steuerschlüssel existiert nicht.");
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		var inactive = GetString(json.RootElement, "Inactive");
		var active = !string.Equals(inactive, "tYES", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(inactive, "Y", StringComparison.OrdinalIgnoreCase);
		return new SapTaxCodeValidation(taxCode.Trim(), true, active, GetString(json.RootElement, "Name"), null);
	}

	public async Task<string> CreateSupplierAsync(SapSupplierCreateRequest request, CancellationToken cancellationToken = default)
	{
		if (!CanWriteBusinessPartners())
			throw new InvalidOperationException("SAP-Lieferantenanlage ist deaktiviert. Erforderlich sind Sap:Mode=write-enabled und Sap:EnableBusinessPartnerWrites=true.");
		ArgumentException.ThrowIfNullOrWhiteSpace(request.CardCode);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
		var payload = new
		{
			CardCode = request.CardCode.Trim(),
			CardName = request.Name.Trim(),
			CardType = "cSupplier",
			FederalTaxID = NullIfEmpty(request.VatId),
			AdditionalID = NullIfEmpty(request.TaxNumber),
			BPAddresses = string.IsNullOrWhiteSpace(request.Street) && string.IsNullOrWhiteSpace(request.City)
				? null
				: new[]
				{
					new
					{
						AddressName = "Rechnung",
						AddressType = "bo_BillTo",
						Street = NullIfEmpty(request.Street),
						ZipCode = NullIfEmpty(request.PostalCode),
						City = NullIfEmpty(request.City),
						Country = string.IsNullOrWhiteSpace(request.CountryCode) ? "DE" : request.CountryCode.Trim().ToUpperInvariant()
					}
				},
			BPBankAccounts = string.IsNullOrWhiteSpace(request.Iban)
				? null
				: new[] { new { IBAN = request.Iban.Trim().Replace(" ", string.Empty, StringComparison.Ordinal) } }
		};
		using var create = await SendJsonAsync(HttpMethod.Post, "BusinessPartners", payload, cancellationToken);
		create.EnsureSuccessStatusCode();
		using var verification = await SendGetAsync($"BusinessPartners('{request.CardCode.Trim().Replace("'", "''", StringComparison.Ordinal)}')?$select=CardCode", cancellationToken);
		verification.EnsureSuccessStatusCode();
		using var verified = JsonDocument.Parse(await verification.Content.ReadAsStreamAsync(cancellationToken));
		return GetString(verified.RootElement, "CardCode");
	}

	public async Task<SapPostingResult> CreatePurchaseInvoiceAsync(SapPurchaseInvoiceRequest request, string actor, CancellationToken cancellationToken = default)
	{
		if (!CanWritePurchaseInvoices())
			throw new InvalidOperationException("SAP-Eingangsrechnungsanlage ist deaktiviert. Erforderlich sind Sap:Mode=write-enabled und Sap:EnablePurchaseInvoiceWrites=true.");
		if (!string.Equals(request.Currency, "EUR", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Die automatische SAP-Buchung unterstützt derzeit ausschließlich EUR.");
		if (request.Lines.Count == 0)
			throw new InvalidOperationException("Mindestens eine Buchungszeile ist erforderlich.");

		var existing = await FindPurchaseInvoiceByProposalAsync(request.ProposalId, cancellationToken);
		if (existing != null) return existing;

		var attachmentEntry = await UploadPdfAttachmentAsync(request.PdfPath, cancellationToken);
		var payload = new
		{
			DocType = "dDocument_Service",
			CardCode = request.SupplierCode,
			NumAtCard = request.InvoiceNumber,
			DocDate = request.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			TaxDate = (request.ServiceDate ?? request.InvoiceDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			DocDueDate = (request.DueDate ?? request.InvoiceDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			DocCurrency = request.Currency,
			AttachmentEntry = attachmentEntry,
			U_NN_ProposalId = request.ProposalId.ToString(),
			U_NN_SourceHash = request.SourceSha256,
			DocumentLines = request.Lines.Select(line => new
			{
				AccountCode = line.Account,
				LineTotal = line.NetAmount,
				TaxCode = line.TaxCode,
				ItemDescription = line.Description
			}).ToArray()
		};

		try
		{
			using var response = await SendJsonAsync(HttpMethod.Post, "PurchaseInvoices", payload, cancellationToken);
			response.EnsureSuccessStatusCode();
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
		{
			var afterTimeout = await FindPurchaseInvoiceByProposalAsync(request.ProposalId, cancellationToken);
			if (afterTimeout != null) return afterTimeout;
			throw new SapOrphanAttachmentException(
				attachmentEntry,
				"SAP-POST war nicht eindeutig erfolgreich. Die UDF-Prüfung fand keinen angelegten Beleg; NovaNein hat nicht erneut gebucht. Der zuvor erzeugte AttachmentEntry wurde als verwaist protokolliert.",
				exception);
		}

		for (var attempt = 0; attempt < 8; attempt++)
		{
			var readback = await FindPurchaseInvoiceByProposalAsync(request.ProposalId, cancellationToken);
			if (readback != null && readback.TransId > 0 && readback.AttachmentEntry == attachmentEntry)
				return readback with { PostedBy = actor };
			await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken);
		}
		throw new InvalidOperationException("SAP hat den neuen Eingangsbeleg nicht vollständig mit TransId und AttachmentEntry zurückgelesen.");
	}

	public async Task<SapPostingResult?> FindPurchaseInvoiceByProposalAsync(Guid proposalId, CancellationToken cancellationToken = default)
	{
		var proposal = proposalId.ToString().Replace("'", "''", StringComparison.Ordinal);
		var filter = Uri.EscapeDataString($"U_NN_ProposalId eq '{proposal}'");
		using var response = await SendGetAsync(
			"PurchaseInvoices?$select=DocEntry,DocNum,TransNum,AttachmentEntry,U_NN_ProposalId,U_NN_SourceHash&$filter=" + filter + "&$top=2",
			cancellationToken);
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		var values = json.RootElement.GetProperty("value");
		if (values.GetArrayLength() == 0) return null;
		if (values.GetArrayLength() > 1)
			throw new InvalidOperationException($"SAP enthält mehrere Eingangsrechnungen für NovaNein-Vorschlag {proposalId}.");
		var item = values[0];
		var canonical = JsonSerializer.Serialize(item);
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
		return new SapPostingResult(
			proposalId,
			item.GetProperty("DocEntry").GetInt32(),
			item.GetProperty("DocNum").GetInt32(),
			item.TryGetProperty("TransNum", out var transNum) && transNum.ValueKind != JsonValueKind.Null ? transNum.GetInt32() : 0,
			item.TryGetProperty("AttachmentEntry", out var attachment) && attachment.ValueKind != JsonValueKind.Null ? attachment.GetInt32() : 0,
			hash,
			DateTimeOffset.UtcNow,
			"sap-readback");
	}

	public async Task<SapDocumentSnapshot?> FindPurchaseInvoiceDuplicateAsync(string cardCode, string invoiceNumber, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cardCode);
		ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
		var card = cardCode.Trim().Replace("'", "''", StringComparison.Ordinal);
		var number = invoiceNumber.Trim().Replace("'", "''", StringComparison.Ordinal);
		var filter = Uri.EscapeDataString($"CardCode eq '{card}' and NumAtCard eq '{number}' and Cancelled eq 'tNO'");
		using var response = await SendGetAsync("PurchaseInvoices?$select=DocEntry&$filter=" + filter + "&$top=2", cancellationToken);
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		var values = json.RootElement.GetProperty("value");
		if (values.GetArrayLength() == 0) return null;
		if (values.GetArrayLength() > 1)
			throw new InvalidOperationException($"SAP enthält mehrere aktive Eingangsrechnungen für Lieferant {cardCode} und Rechnungsnummer {invoiceNumber}.");
		return await GetDocumentAsync(SapDocumentKind.PurchaseInvoice, values[0].GetProperty("DocEntry").GetInt32(), cancellationToken);
	}

	private async Task<int> UploadPdfAttachmentAsync(string localPdfPath, CancellationToken cancellationToken)
	{
		if (!CanWriteAttachments())
			throw new InvalidOperationException("SAP-Anhangsschreiben ist deaktiviert.");
		var fullPath = ValidateAttachmentSource(localPdfPath);
		var sourcePath = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("Der PDF-Quellordner fehlt.");
		var fileName = Path.GetFileNameWithoutExtension(fullPath);
		var payload = new
		{
			Attachments2_Lines = new[]
			{
				new
				{
					SourcePath = sourcePath,
					FileName = fileName,
					FileExtension = "pdf",
					AttachmentDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					Override = "tNO",
					FreeText = "NovaNein Vorschlag: " + fileName
				}
			}
		};
		using var create = await SendJsonAsync(HttpMethod.Post, "Attachments2", payload, cancellationToken);
		create.EnsureSuccessStatusCode();
		using var created = JsonDocument.Parse(await create.Content.ReadAsStreamAsync(cancellationToken));
		var entry = created.RootElement.GetProperty("AbsoluteEntry").GetInt32();
		if (!await AttachmentContainsFileAsync(entry, fileName, "pdf", cancellationToken))
			throw new InvalidOperationException("SAP hat den hochgeladenen PDF-Anhang nicht bestätigt.");
		return entry;
	}

	private async Task EnsureLoginAsync(CancellationToken cancellationToken)
	{
		if (_sessionId != null)
		{
			return;
		}
		await _loginLock.WaitAsync(cancellationToken);
		try
		{
			if (_sessionId != null)
			{
				return;
			}
			string company = configuration["Sap:CompanyDatabase"] ?? throw new InvalidOperationException("Sap:CompanyDatabase fehlt.");
			string user = configuration["Sap:UserName"] ?? throw new InvalidOperationException("SAP-Benutzername fehlt im Secretspeicher.");
			string password = configuration["Sap:Password"] ?? throw new InvalidOperationException("SAP-Passwort fehlt im Secretspeicher.");
			using var loginContent = JsonContent.Create(new Dictionary<string, string>
			{
				["CompanyDB"] = company,
				["UserName"] = user,
				["Password"] = password
			}, options: SapJsonOptions);
			using HttpResponseMessage response = await httpClient.PostAsync("Login", loginContent, cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				string detail = await response.Content.ReadAsStringAsync();
				throw new InvalidOperationException($"SAP Service Layer Login fehlgeschlagen (HTTP {response.StatusCode}): {detail}");
			}
			using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
			_sessionId = payload.RootElement.GetProperty("SessionId").GetString() ?? throw new InvalidDataException("SAP Login lieferte keine SessionId.");
		}
		finally
		{
			_loginLock.Release();
		}
	}

	private HttpRequestMessage NewRequest(HttpMethod method, string relativeUri)
	{
		HttpRequestMessage request = new HttpRequestMessage(method, relativeUri);
		request.Headers.TryAddWithoutValidation("Cookie", "B1SESSION=" + _sessionId);
		return request;
	}

	private async Task<HttpResponseMessage> SendGetAsync(string relativeUri, CancellationToken cancellationToken)
	{
		await EnsureLoginAsync(cancellationToken);
		using HttpRequestMessage request = NewRequest(HttpMethod.Get, relativeUri);
		HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
		HttpStatusCode statusCode = response.StatusCode;
		if ((statusCode != HttpStatusCode.Unauthorized && statusCode != HttpStatusCode.Forbidden) || 1 == 0)
		{
			return response;
		}
		response.Dispose();
		await _loginLock.WaitAsync(cancellationToken);
		try
		{
			_sessionId = null;
		}
		finally
		{
			_loginLock.Release();
		}
		await EnsureLoginAsync(cancellationToken);
		using HttpRequestMessage retry = NewRequest(HttpMethod.Get, relativeUri);
		return await httpClient.SendAsync(retry, cancellationToken);
	}

	private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string relativeUri, object payload, CancellationToken cancellationToken)
	{
		await EnsureLoginAsync(cancellationToken);
		HttpResponseMessage response = await SendJsonOnceAsync(method, relativeUri, payload, cancellationToken);
		HttpStatusCode statusCode = response.StatusCode;
		if ((statusCode != HttpStatusCode.Unauthorized && statusCode != HttpStatusCode.Forbidden) || 1 == 0)
		{
			return response;
		}
		response.Dispose();
		await _loginLock.WaitAsync(cancellationToken);
		try
		{
			_sessionId = null;
		}
		finally
		{
			_loginLock.Release();
		}
		await EnsureLoginAsync(cancellationToken);
		return await SendJsonOnceAsync(method, relativeUri, payload, cancellationToken);
	}

	private async Task<HttpResponseMessage> SendJsonOnceAsync(HttpMethod method, string relativeUri, object payload, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = NewRequest(method, relativeUri);
		request.Content = JsonContent.Create(payload, options: SapJsonOptions);
		return await httpClient.SendAsync(request, cancellationToken);
	}

	private async Task<bool> AttachmentContainsPdfAsync(int attachmentEntry, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendGetAsync($"Attachments2({attachmentEntry})", cancellationToken);
		response.EnsureSuccessStatusCode();
		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		return ContainsPdfExtension(json.RootElement);
	}

	private async Task<bool> AttachmentContainsFileAsync(int attachmentEntry, string fileName, string extension, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendGetAsync($"Attachments2({attachmentEntry})", cancellationToken);
		response.EnsureSuccessStatusCode();
		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		return ContainsAttachmentFile(json.RootElement, fileName, extension);
	}

	private static bool ContainsPdfExtension(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				if ((property.NameEquals("FileExtension") || property.NameEquals("FileExt")) && property.Value.ValueKind == JsonValueKind.String && string.Equals(property.Value.GetString(), "pdf", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				if (ContainsPdfExtension(property.Value))
				{
					return true;
				}
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in element.EnumerateArray())
			{
				if (ContainsPdfExtension(item))
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool ContainsAttachmentFile(JsonElement element, string expectedFileName, string expectedExtension)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			JsonElement name;
			bool fileNameMatches = element.TryGetProperty("FileName", out name) && string.Equals(name.GetString(), expectedFileName, StringComparison.Ordinal);
			JsonElement extension;
			bool extensionMatches = element.TryGetProperty("FileExtension", out extension) && string.Equals(extension.GetString(), expectedExtension, StringComparison.OrdinalIgnoreCase);
			if (fileNameMatches && extensionMatches)
			{
				return true;
			}
			foreach (JsonProperty item2 in element.EnumerateObject())
			{
				if (ContainsAttachmentFile(item2.Value, expectedFileName, expectedExtension))
				{
					return true;
				}
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in element.EnumerateArray())
			{
				if (ContainsAttachmentFile(item, expectedFileName, expectedExtension))
				{
					return true;
				}
			}
		}
		return false;
	}

	private string ValidateAttachmentSource(string localPdfPath)
	{
		if (string.IsNullOrWhiteSpace(localPdfPath))
		{
			throw new ArgumentException("Ein PDF-Pfad ist erforderlich.", "localPdfPath");
		}
		if (!string.Equals(Path.GetExtension(localPdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Nur PDF-Dateien dürfen an SAP angehängt werden.");
		}
		string fullPath = Path.GetFullPath(localPdfPath);
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException("Die anzuhängende PDF wurde nicht gefunden.", fullPath);
		}
		string configuredRoot = configuration["Sap:AttachmentSourceRoot"];
		if (string.IsNullOrWhiteSpace(configuredRoot))
		{
			throw new InvalidOperationException("Sap:AttachmentSourceRoot muss für SAP-Anhänge ausdrücklich konfiguriert sein.");
		}
		string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot)) + Path.DirectorySeparatorChar;
		if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Die PDF liegt außerhalb von Sap:AttachmentSourceRoot und wird deshalb nicht an SAP übertragen.");
		}
		return fullPath;
	}

	private static DateOnly ParseServiceLayerDate(JsonElement element, string missingMessage)
	{
		string value = element.GetString() ?? throw new InvalidDataException(missingMessage);
		if (DateTimeOffset.TryParse(value, out var timestamp))
		{
			return DateOnly.FromDateTime(timestamp.Date);
		}
		if (DateOnly.TryParse(value, out var date))
		{
			return date;
		}
		throw new InvalidDataException("Ungültiges SAP-Datum: " + value);
	}

	private static SapDocumentSnapshot ReadSnapshot(JsonElement root, SapDocumentKind kind)
	{
		int sapDocNum = root.GetProperty("DocNum").GetInt32();
		bool flag = ((kind == SapDocumentKind.Invoice || kind == SapDocumentKind.CreditNote) ? true : false);
		JsonElement supplierInvoiceNumber;
		string invoiceNumber = (flag ? sapDocNum.ToString(CultureInfo.InvariantCulture) : (root.TryGetProperty("NumAtCard", out supplierInvoiceNumber) ? (supplierInvoiceNumber.GetString() ?? string.Empty) : string.Empty));
		JsonElement creation;
		DateOnly? entryDate = ((root.TryGetProperty("CreationDate", out creation) && creation.ValueKind == JsonValueKind.String) ? new DateOnly?(ParseServiceLayerDate(creation, "SAP CreationDate fehlt.")) : ((DateOnly?)null));
		JsonElement attachment;
		JsonElement transId;
		JsonElement comments;
		return new SapDocumentSnapshot(kind, root.GetProperty("DocEntry").GetInt32(), sapDocNum, root.GetProperty("CardCode").GetString() ?? string.Empty, root.GetProperty("CardName").GetString() ?? string.Empty, invoiceNumber, ParseServiceLayerDate(root.GetProperty("DocDate"), "SAP DocDate fehlt."), root.GetProperty("DocTotal").GetDecimal(), root.GetProperty("DocCurrency").GetString() ?? string.Empty, (root.TryGetProperty("AttachmentEntry", out attachment) && attachment.ValueKind != JsonValueKind.Null) ? new int?(attachment.GetInt32()) : ((int?)null), entryDate, (root.TryGetProperty("TransNum", out var transNum) && transNum.ValueKind != JsonValueKind.Null) ? new int?(transNum.GetInt32()) : ((int?)null), (root.TryGetProperty("Comments", out comments) && comments.ValueKind == JsonValueKind.String) ? comments.GetString() : null);
	}

	private static string EntityName(SapDocumentKind kind)
	{
		return kind switch
		{
			SapDocumentKind.PurchaseInvoice => "PurchaseInvoices",
			SapDocumentKind.Invoice => "Invoices",
			SapDocumentKind.PurchaseCreditNote => "PurchaseCreditNotes",
			SapDocumentKind.CreditNote => "CreditNotes",
			_ => throw new ArgumentOutOfRangeException("kind"),
		};
	}

	private bool CanWriteAttachments()
	{
		bool enabled = default(bool);
		return string.Equals(configuration["Sap:Mode"], "write-enabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(configuration["Sap:EnableAttachments2Writes"], out enabled) && enabled;
	}

	private bool CanWritePurchaseInvoices()
		=> IsSapWriteMode() && configuration.GetValue("Sap:EnablePurchaseInvoiceWrites", false);

	private bool CanWriteBusinessPartners()
		=> IsSapWriteMode() && configuration.GetValue("Sap:EnableBusinessPartnerWrites", false);

	private bool IsSapWriteMode()
		=> string.Equals(configuration["Sap:Mode"], "write-enabled", StringComparison.OrdinalIgnoreCase);

	private static string GetString(JsonElement element, string propertyName)
		=> element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;

	private static string? FirstString(JsonElement element, string collectionName, string propertyName)
	{
		if (!element.TryGetProperty(collectionName, out var collection) || collection.ValueKind != JsonValueKind.Array)
			return null;
		foreach (var item in collection.EnumerateArray())
		{
			var value = GetString(item, propertyName);
			if (!string.IsNullOrWhiteSpace(value)) return value;
		}
		return null;
	}

	private static void AddExactScore(
		string? expected,
		string? actual,
		decimal points,
		string reason,
		ICollection<string> reasons,
		ref decimal score)
	{
		if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return;
		if (!string.Equals(NormalizeMatchValue(expected), NormalizeMatchValue(actual), StringComparison.Ordinal)) return;
		score += points;
		reasons.Add(reason);
	}

	private static string NormalizeMatchValue(string value)
		=> new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

	private static string? NullIfEmpty(string? value)
		=> string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
