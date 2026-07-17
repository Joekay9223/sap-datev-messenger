using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class ReconciliationService(AccountingImportStore imports, ISapServiceLayerClient sap, DocumentStore documents, IConfiguration configuration)
{
	public async Task<ReconciliationPage> ListAsync(Guid? batchId, ReconciliationStatus? status, int page, int pageSize, CancellationToken ct = default(CancellationToken))
	{
		AccountingImportBatch accountingImportBatch;
		if (batchId.HasValue)
		{
			Guid id = batchId.GetValueOrDefault();
			accountingImportBatch = await imports.GetAsync(id, includeRows: true, ct);
		}
		else
		{
			accountingImportBatch = await imports.GetActiveAsync(ct);
		}
		AccountingImportBatch batch = accountingImportBatch;
		if ((object)batch == null || batch.Status != AccountingImportStatus.Confirmed)
		{
			throw new InvalidOperationException("Es gibt noch keinen bestätigten DATEV-Import für den Abgleich.");
		}
		List<ReconciliationItem> all = await CalculateAsync(batch, ct);
		if (status.HasValue)
		{
			all = all.Where((ReconciliationItem x) => x.Status == status).ToList();
		}
		page = Math.Max(1, page);
		pageSize = Math.Clamp(pageSize, 1, 200);
		int total = all.Count;
		return new ReconciliationPage(all.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), batch.Id, page, pageSize, total, (page * pageSize < total) ? new int?(page + 1) : ((int?)null), (from x in all
			group x by x.Status.ToString()).ToDictionary((IGrouping<string, ReconciliationItem> x) => x.Key, (IGrouping<string, ReconciliationItem> x) => x.Count()));
	}

	public async Task<ReconciliationItem?> GetAsync(string id, CancellationToken ct = default(CancellationToken))
	{
		return (await ListAsync(null, null, 1, 20000, ct)).Items.FirstOrDefault((ReconciliationItem x) => x.Id == id);
	}

	public async Task<ReconciliationItem> DecideAsync(string id, ReconciliationDecisionRequest request, string actor, CancellationToken ct = default(CancellationToken))
	{
		ReconciliationItem current = (await GetAsync(id, ct)) ?? throw new KeyNotFoundException("Der Abgleichsfall wurde nicht gefunden.");
		if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(current.ExpectedHash), Encoding.ASCII.GetBytes(request.ExpectedHash)))
		{
			throw new InvalidOperationException("Der Abgleich wurde inzwischen geändert. Bitte neu laden.");
		}
		await imports.SaveDecisionAsync(id, current.BatchId, request, actor, ct);
		return await GetAsync(id, ct);
	}

	private async Task<List<ReconciliationItem>> CalculateAsync(AccountingImportBatch batch, CancellationToken ct)
	{
		DateOnly from = batch.PeriodStart ?? DateOnly.FromDateTime(DateTime.Today.AddMonths(-1));
		DateOnly to = batch.PeriodEnd ?? DateOnly.FromDateTime(DateTime.Today);
		List<SapDocumentSnapshot> snapshots = new List<SapDocumentSnapshot>();
		SapDocumentKind[] values = Enum.GetValues<SapDocumentKind>();
		foreach (SapDocumentKind kind in values)
		{
			List<SapDocumentSnapshot> list = snapshots;
			list.AddRange(await sap.ListDocumentsAsync(kind, from.AddDays(-3), to.AddDays(3), ct));
		}
		List<DatevBookingRow> rows = batch.Rows?.ToList() ?? new List<DatevBookingRow>();
		HashSet<Guid> used = new HashSet<Guid>();
		List<ReconciliationItem> result = new List<ReconciliationItem>();
		foreach (SapDocumentSnapshot s in snapshots)
		{
			string normalized = DatevBookingCsvParser.NormalizeReference(s.InvoiceNumber);
			List<DatevBookingRow> compatible = rows.Where((DatevBookingRow r) => r.NormalizedReference == normalized && TypeMatches(s.Kind, r.DebitCredit)).ToList();
			List<DatevBookingRow> exact = compatible.Where((DatevBookingRow r) => r.Amount == decimal.Abs(s.GrossAmount) && string.Equals(r.Currency, s.Currency, StringComparison.OrdinalIgnoreCase)).ToList();
			List<string> reasons = new List<string>();
			ReconciliationStatus status;
			List<DatevBookingRow> candidates;
			if (exact.Count == 1)
			{
				status = ReconciliationStatus.Matched;
				candidates = exact;
				reasons.Add("Rechnungsnummer, Betrag, Währung und Belegart stimmen eindeutig überein.");
				used.Add(exact[0].Id);
			}
			else if (exact.Count > 1)
			{
				status = ReconciliationStatus.Ambiguous;
				candidates = exact;
				reasons.Add("Mehrere DATEV-Zeilen erfüllen die verbindlichen Kriterien.");
			}
			else if (compatible.Count > 0)
			{
				status = ReconciliationStatus.AmountOrCurrencyMismatch;
				candidates = compatible;
				reasons.Add("Rechnungsnummer und Belegart passen, Betrag oder Währung weichen jedoch ab.");
			}
			else
			{
				status = ReconciliationStatus.InSapNotInDatev;
				candidates = new List<DatevBookingRow>();
				reasons.Add("Für den SAP-Beleg wurde keine passende DATEV-Zeile gefunden.");
			}
			DocumentDirection direction = Direction(s.Kind);
			bool pdf = (object)(await documents.GetBySapAsync(direction, s.DocEntry, ct)) != null;
			if (!pdf && status == ReconciliationStatus.Matched)
			{
				status = ReconciliationStatus.PdfMissing;
				reasons.Add("Der Beleg ist fachlich abgeglichen, aber die PDF fehlt im NovaNein-Archiv.");
			}
			DatevBookingRow row = candidates.FirstOrDefault();
			string id = $"sap:{s.Kind}:{s.DocEntry}";
			string hash = Hash(batch.FileSha256, s.Kind.ToString(), s.DocEntry.ToString(), s.DocNum.ToString(), row?.RowSha256 ?? "-");
			(string, string, string, DateTimeOffset)? decision = await imports.LatestDecisionAsync(id, batch.Id, ct);
			DateTimeOffset? decidedAt = null;
			string decidedBy = null;
			string decisionReason = null;
			if (decision.HasValue)
			{
				(string, string, string, DateTimeOffset) d = decision.GetValueOrDefault();
				if (d.Item1 == hash)
				{
					status = ReconciliationStatus.ManuallyDecided;
					decidedAt = d.Item4;
					decidedBy = d.Item3;
					decisionReason = d.Item2;
					reasons.Add("Ein Reviewer hat diesen Stand manuell entschieden.");
				}
			}
			result.Add(new ReconciliationItem(id, batch.Id, s.Kind, (direction == DocumentDirection.Incoming) ? "incoming" : "outgoing", s.DocEntry, s.DocNum, s.InvoiceNumber, s.BusinessPartnerName, s.DocumentDate, s.GrossAmount, s.Currency, row?.Id, row?.Amount, row?.Currency ?? "", row?.Account ?? "", row?.Reference1 ?? "", status, reasons, hash, pdf, decidedAt, decidedBy, decisionReason));
		}
		foreach (DatevBookingRow row2 in rows.Where((DatevBookingRow x) => !used.Contains(x.Id)))
		{
			if (!result.Any((ReconciliationItem x) => x.DatevRowId == row2.Id))
			{
				string id2 = $"datev:{row2.Id}";
				string hash2 = Hash(batch.FileSha256, row2.RowSha256);
				result.Add(new ReconciliationItem(id2, batch.Id, null, null, null, null, row2.Reference1, row2.PartnerAccount, row2.DocumentDate, null, "", row2.Id, row2.Amount, row2.Currency, row2.Account, row2.Reference1, ReconciliationStatus.InDatevNotInSap, new[] { "Für diese DATEV-Zeile wurde kein SAP-Beleg gefunden." }, hash2, PdfPresent: false));
			}
		}
		return (from x in result
			orderby x.DocumentDate descending, x.Id
			select x).ToList();
	}

	private bool TypeMatches(SapDocumentKind kind, string debitCredit)
	{
		string incoming = configuration["Accounting:IncomingDebitCredit"] ?? "H";
		string outgoing = configuration["Accounting:OutgoingDebitCredit"] ?? "S";
		return debitCredit == kind switch
		{
			SapDocumentKind.PurchaseInvoice => incoming,
			SapDocumentKind.Invoice => outgoing,
			SapDocumentKind.PurchaseCreditNote => Opposite(incoming),
			SapDocumentKind.CreditNote => Opposite(outgoing),
			_ => "",
		};
	}

	private static string Opposite(string value)
	{
		if (!(value == "S"))
		{
			return "S";
		}
		return "H";
	}

	private static DocumentDirection Direction(SapDocumentKind kind)
	{
		if ((kind != SapDocumentKind.PurchaseInvoice && kind != SapDocumentKind.PurchaseCreditNote) || 1 == 0)
		{
			return DocumentDirection.Outgoing;
		}
		return DocumentDirection.Incoming;
	}

	private static string Hash(params string[] values)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', values))));
	}
}
