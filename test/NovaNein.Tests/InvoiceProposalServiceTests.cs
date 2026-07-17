using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class InvoiceProposalServiceTests
{
	[Fact]
	public void Treats_skin_protection_supplies_as_a_simple_cost_invoice()
	{
		var facts = new ExtractedInvoiceFacts(
			"991930407",
			"Example Supplier GmbH",
			"DE257224517",
			164.24m,
			"EUR",
			new DateOnly(2026, 7, 16),
			true,
			false,
			false,
			"Hautschutzcreme mit Online-Bestellnummer",
			Lines:
			[
				new ExtractedInvoiceLine(
					1,
					"Art.-Nr. 107027 – Stokoderm AQUA PURE Hautschutzcreme; 3 Kartuschen",
					135.76m,
					25.79m,
					19m,
					"6850",
					"V2",
					true)
			],
			HasPurchaseOrderReference: true,
			HasGoodsCharacteristics: true);

		var classified = InventoryGoodsClassifier.Apply(facts);

		Assert.False(classified.HasGoodsCharacteristics);
		Assert.False(classified.HasPurchaseOrderReference);
		Assert.False(Assert.Single(classified.Lines!).LooksLikeGoods);
	}

	[Theory]
	[InlineData("Bio Feigen getrocknet, 10 kg", true)]
	[InlineData("Inventory item, 25 kg", true)]
	[InlineData("Bedruckte Standbodenbeutel 500 g", true)]
	[InlineData("Etiketten für Verkaufsverpackung", true)]
	[InlineData("Bio Reismehl, 25 kg", true)]
	[InlineData("Organic rice flour, 25 kg", true)]
	[InlineData("Inventory item, 25 kg; Commodity code: TEST-CODE", true)]
	[InlineData("Hautschutzcreme in Kartusche", false)]
	[InlineData("Versandkosten per Paketdienst", false)]
	public void Only_inventory_managed_goods_and_packaging_require_item_posting(string description, bool expected)
	{
		Assert.Equal(expected, InventoryGoodsClassifier.RequiresItemPosting(description));
	}

	[Fact]
	public void Stored_ebro_proposal_can_be_safely_reclassified_without_reextracting_the_pdf()
	{
		var proposal = new InvoiceProposal(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "incoming", MailSourceStatuses.ProposalReady, "green",
			"invoice", "690074655", "Ebro Ingredients", "72223", null, null, null,
			new DateOnly(2026, 7, 13), null, null, 100m, 0m, 100m, "EUR", false, false, false,
			"Ebro invoice", "sha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null,
			["Physische Position erkannt, aber keine bestandsgeführte Ware; Verarbeitung als einfache Kostenrechnung ist zulässig."],
			[new InvoiceProposalLine(1, "Inventory item, 25 kg; Commodity code: TEST-CODE", 100m, 0m, 0m, "3320", "V0", "SAP-Historie", 0.9m, false)]);

		var classified = InvoiceProposalService.ReclassifyStoredInventoryProposal(proposal);

		Assert.Equal(MailSourceStatuses.Blocked, classified.Status);
		Assert.Equal("red", classified.Signal);
		Assert.True(classified.HasGoodsCharacteristics);
		Assert.True(Assert.Single(classified.Lines).LooksLikeGoods);
		Assert.Contains(classified.Findings, finding => finding.StartsWith("Bestandsgeführte Ware erkannt", StringComparison.Ordinal));
		Assert.DoesNotContain(classified.Findings, finding => finding.StartsWith("Physische Position erkannt", StringComparison.Ordinal));
	}

	[Fact]
	public void Posted_sap_history_has_priority_over_ai_account_labels()
	{
		var facts = new ExtractedInvoiceFacts(
			"26070189",
			"Example Logistics GmbH",
			"DE222432970",
			327.25m,
			"EUR",
			new DateOnly(2026, 7, 10),
			true,
			false,
			false,
			"Containertransport",
			NetAmount: 275m,
			TaxAmount: 52.25m,
			Lines:
			[
				new ExtractedInvoiceLine(
					1,
					"Rundlauf und Dieselzuschlag",
					275m,
					52.25m,
					19m,
					"Fracht- und Transportkosten",
					"Vorsteuer 19 %",
					false)
			]);
		var history = new[]
		{
			new SapCodingCandidate("5800", "V2", "Container", 718, 0.98m, "SAP-Historie")
		};
		var findings = new List<string>();

		var line = Assert.Single(InvoiceProposalService.BuildLines(facts, history, findings));

		Assert.Equal("5800", line.Account);
		Assert.Equal("V2", line.TaxCode);
		Assert.Equal("SAP-Historie", line.SuggestionSource);
		Assert.Empty(findings);
	}

	[Fact]
	public void Outgoing_proposals_use_complete_posted_sap_lines_instead_of_ai_coding()
	{
		var accounting = OutgoingAccounting();

		var line = Assert.Single(InvoiceProposalService.BuildOutgoingLines(accounting));

		Assert.Equal(1, line.LineNumber);
		Assert.Equal("8400", line.Account);
		Assert.Equal("A2", line.TaxCode);
		Assert.Equal("SAP-Readback", line.SuggestionSource);
		Assert.Equal(1m, line.Confidence);
	}

	[Fact]
	public void Outgoing_match_is_blocked_when_pdf_and_sap_amounts_differ()
	{
		var accounting = OutgoingAccounting();
		var facts = new ExtractedInvoiceFacts(
			"900003", "Example Customer GmbH", "DE999", 120m, "EUR", new DateOnly(2026, 7, 17),
			true, false, false, "Ausgangsrechnung",
			NetAmount: 100m, TaxAmount: 20m,
			IssuerName: "Example Company GmbH", IssuerVatId: "DE000000000",
			RecipientName: "Example Customer GmbH", RecipientVatId: "DE999");
		var findings = new List<string>();

		InvoiceProposalService.AddOutgoingSapFindings(facts, accounting.Snapshot, accounting, findings);

		Assert.Contains(findings, finding => finding.Contains("PDF-Bruttobetrag", StringComparison.Ordinal));
	}

	private static SapAccountingDocument OutgoingAccounting()
	{
		var snapshot = new SapDocumentSnapshot(
			SapDocumentKind.Invoice, 4711, 900003, "C100", "Example Customer GmbH", "900003",
			new DateOnly(2026, 7, 17), 119m, "EUR", null, TransId: 9001);
		return new SapAccountingDocument(
			snapshot,
			9001,
			"10001",
			"DE999",
			"Kundenweg 1",
			"20095",
			"Hamburg",
			"Example Company GmbH",
			"DE000000000",
			"Example Street 1",
			"12345",
			"Example City",
			[new SapDocumentLine(0, "Ware", 1m, 100m, 19m, "A2", 19m, "8400", "EUR")],
			[new SapTaxBreakdown(0, "A2", 19m, 100m, 19m, "EUR")],
			[
				new SapJournalLine(0, "10001", "8400", "S", 119m, 0m, ""),
				new SapJournalLine(1, "8400", "10001", "H", 0m, 100m, ""),
				new SapJournalLine(2, "1776", "10001", "H", 0m, 19m, "")
			],
			[new DatevBookingMapping("A2", "3", "8400", new DateOnly(2026, 1, 1), null, "test", "hash")],
			new string('A', 64));
	}
}
