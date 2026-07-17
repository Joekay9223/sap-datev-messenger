using System.Collections.Generic;
using System.Linq;

namespace NovaNein.Server;

public sealed record SapAccountingDocument(SapDocumentSnapshot Snapshot, int? TransId, string? PartnerAccountNumber, string? PartnerVatId, string? PartnerStreet, string? PartnerZip, string? PartnerCity, string? CompanyName, string? CompanyVatId, string? CompanyStreet, string? CompanyZip, string? CompanyCity, IReadOnlyList<SapDocumentLine> Lines, IReadOnlyList<SapTaxBreakdown> Taxes, IReadOnlyList<SapJournalLine> JournalLines, IReadOnlyList<DatevBookingMapping> DatevMappings, string SourceHash)
{
	public bool IsComplete => CompletenessIssues.Count == 0;

	public IReadOnlyList<string> CompletenessIssues
	{
		get
		{
			var issues = new List<string>();
			if (Snapshot.DocEntry <= 0 || Snapshot.DocNum <= 0) issues.Add("SAP-Belegidentität fehlt.");
			if (Snapshot.GrossAmount < 0m) issues.Add("SAP-Bruttobetrag ist ungültig.");
			if (string.IsNullOrWhiteSpace(Snapshot.Currency)) issues.Add("SAP-Währung fehlt.");
			if (!TransId.HasValue || TransId.Value <= 0) issues.Add("SAP-Journalreferenz fehlt.");
			if (string.IsNullOrWhiteSpace(PartnerAccountNumber)) issues.Add("SAP-Geschäftspartnerkonto fehlt.");
			if (string.IsNullOrWhiteSpace(CompanyName)) issues.Add("SAP-Firmenname fehlt.");
			if (Lines.Count == 0) issues.Add("SAP-Positionen fehlen.");
			if (Taxes.Count == 0) issues.Add("SAP-Steuerzeilen fehlen.");
			if (JournalLines.Count == 0) issues.Add("SAP-Journalzeilen fehlen.");
			if (DatevMappings.Count == 0) issues.Add("SAP-AVT1-DATEV-Zuordnung fehlt.");
			// Bei SAP-Gutschriften können Debit/Credit mit negativen Beträgen geliefert
			// werden und das technische Soll/Haben-Feld leer sein. Die Seite wird bei
			// der DATEV-Erzeugung sicher aus Vorzeichen und Betrag abgeleitet.
			if (JournalLines.Any(line => (line.Debit != 0m || line.Credit != 0m) && string.IsNullOrWhiteSpace(line.Account)))
				issues.Add("Eine werttragende SAP-Journalzeile ist unvollständig.");
			return issues;
		}
	}
}
