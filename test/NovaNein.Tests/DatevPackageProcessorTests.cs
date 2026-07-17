using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class DatevPackageProcessorTests
{
	[Theory]
	[InlineData(null, "12345")]
	[InlineData("", "12345")]
	[InlineData("   ", "12345")]
	[InlineData("1109", "1109")]
	public void Uses_default_supplier_zip_only_when_sap_zip_is_missing(string? sapZip, string expected)
	{
		Assert.Equal(expected, DatevPackageProcessor.ResolveSupplierZip(sapZip));
	}

	[Fact]
	public void Uses_exact_journal_account_before_direction_fallback()
	{
		var journal = new[]
		{
			new SapJournalLine(0, "1400", "4400", "S", 119m, 0m, ""),
			new SapJournalLine(1, "4400", "1400", "H", 0m, 100m, ""),
			new SapJournalLine(2, "1776", "1400", "H", 0m, 19m, "")
		};

		Assert.Equal("H", DatevPackageProcessor.ResolveDebitCredit(journal, "4400", DocumentDirection.Outgoing));
	}

	[Fact]
	public void Falls_back_to_the_invoice_side_when_sap_posts_through_a_collective_account()
	{
		var outgoingJournal = new[]
		{
			new SapJournalLine(0, "1400", "_SYS", "S", 119m, 0m, ""),
			new SapJournalLine(1, "_SYS0001", "1400", "H", 0m, 119m, "")
		};
		var incomingJournal = new[]
		{
			new SapJournalLine(0, "_SYS0002", "1600", "S", 100m, 0m, ""),
			new SapJournalLine(1, "1600", "_SYS", "H", 0m, 100m, "")
		};

		Assert.Equal("H", DatevPackageProcessor.ResolveDebitCredit(outgoingJournal, "4400", DocumentDirection.Outgoing));
		Assert.Equal("S", DatevPackageProcessor.ResolveDebitCredit(incomingJournal, "6300", DocumentDirection.Incoming));
	}

	[Fact]
	public void Refuses_direction_fallback_when_the_expected_journal_side_is_absent()
	{
		var journal = new[] { new SapJournalLine(0, "1400", "", "S", 100m, 0m, "") };

		Assert.Null(DatevPackageProcessor.ResolveDebitCredit(journal, "4400", DocumentDirection.Outgoing));
	}

	[Fact]
	public void Derives_the_reversed_side_from_negative_credit_note_journal_values()
	{
		var journal = new[]
		{
			new SapJournalLine(0, "1600", "3400", "", 0m, -1740.85m, ""),
			new SapJournalLine(1, "3400", "1600", "", -1462.90m, 0m, ""),
			new SapJournalLine(2, "1576", "1600", "", -277.95m, 0m, "")
		};

		Assert.Equal("H", DatevPackageProcessor.ResolveDebitCredit(journal, "3400", DocumentDirection.Incoming, creditNote: true));
		Assert.Equal("S", DatevPackageProcessor.ResolveDebitCredit(journal, "1600", DocumentDirection.Incoming, creditNote: true));
	}

	[Fact]
	public void Accepts_signed_credit_note_journal_lines_without_a_separate_side_marker()
	{
		var accounting = new SapAccountingDocument(
			new SapDocumentSnapshot(SapDocumentKind.PurchaseCreditNote, 510, 80003, "L100", "Testlieferant", "G-EXAMPLE-003", new DateOnly(2026, 7, 10), 1740.85m, "EUR", null, TransId: 123),
			123, "70001", "DE000000001", "Straße 1", "12345", "Ort", "Example Company GmbH", "DE000000000", "Example Street 1", "12345", "Example City",
			[new SapDocumentLine(0, "Gutschrift", 1m, 1462.90m, 277.95m, "V2", 19m, "3400", "EUR")],
			[new SapTaxBreakdown(0, "V2", 19m, 1462.90m, 277.95m, "EUR")],
			[new SapJournalLine(0, "1600", "3400", "", 0m, -1740.85m, ""), new SapJournalLine(1, "3400", "1600", "", -1740.85m, 0m, "")],
			[new DatevBookingMapping("V2", "9", "3400", new DateOnly(2020, 1, 1), null, "test", "hash")], "hash");

		Assert.True(accounting.IsComplete, string.Join(" ", accounting.CompletenessIssues));
	}

	[Fact]
	public void Treats_e13_reverse_charge_as_zero_document_tax_but_keeps_parallel_journal_tax()
	{
		var accounting = new SapAccountingDocument(
			new SapDocumentSnapshot(SapDocumentKind.PurchaseCreditNote, 513, 319512, "L100", "MSC Germany", "DEHAMPM26008970C", new DateOnly(2026, 7, 14), 378m, "EUR", null, TransId: 124),
			124, "70002", "DE000000001", "Straße 1", "12345", "Ort", "Example Company GmbH", "DE000000000", "Example Street 1", "12345", "Example City",
			[new SapDocumentLine(0, "MSC", 1m, 378m, 71.82m, "E13", 19m, "5800", "EUR", IsReverseCharge: true)],
			[new SapTaxBreakdown(0, "E13", 19m, 378m, 71.82m, "EUR", ReverseChargePercent: 100m, ReverseChargeTaxAmount: 71.82m)],
			[new SapJournalLine(0, "3310", "5800", "", 0m, -378m, ""), new SapJournalLine(1, "1407", "72011", "", -71.82m, 0m, ""), new SapJournalLine(2, "5800", "72011", "", -378m, 0m, ""), new SapJournalLine(3, "3837", "72011", "", 0m, -71.82m, "")],
			[new DatevBookingMapping("E13", "506", "5800", new DateOnly(2026, 7, 14), null, "SAP AVT1", "hash")], "hash");

		var totals = DatevPackageProcessor.CalculateEffectiveAccountingTotals(accounting, creditNote: true, gross: 378m);

		Assert.Equal(0m, totals.EffectiveTax);
		Assert.Equal(0m, totals.EffectiveTaxBreakdown);
		Assert.Equal(71.82m, totals.ReverseChargeTax);
		Assert.Equal(449.82m, totals.JournalDebit);
		Assert.Equal(449.82m, totals.JournalCredit);
	}

	[Fact]
	public void Infers_reverse_charge_from_net_gross_and_parallel_balanced_journal_when_sap_metadata_is_empty()
	{
		var accounting = new SapAccountingDocument(
			new SapDocumentSnapshot(SapDocumentKind.PurchaseCreditNote, 513, 319512, "L100", "MSC Germany", "DEHAMPM26008970C", new DateOnly(2026, 7, 14), 378m, "EUR", null, TransId: 124),
			124, "70002", "DE000000001", "Straße 1", "12345", "Ort", "Example Company GmbH", "DE000000000", "Example Street 1", "12345", "Example City",
			[new SapDocumentLine(0, "MSC", 1m, 378m, 71.82m, "E13", 19m, "5800", "EUR")],
			[new SapTaxBreakdown(0, "E13", 19m, 378m, 71.82m, "EUR")],
			[new SapJournalLine(0, "3310", "5800", "", 0m, -378m, ""), new SapJournalLine(1, "1407", "72011", "", -71.82m, 0m, ""), new SapJournalLine(2, "5800", "72011", "", -378m, 0m, ""), new SapJournalLine(3, "3837", "72011", "", 0m, -71.82m, "")],
			[new DatevBookingMapping("E13", "506", "5800", new DateOnly(2026, 7, 14), null, "SAP AVT1", "hash")], "hash");

		Assert.True(DatevPackageProcessor.IsReverseChargeDocument(accounting, 378m, creditNote: true));
	}
}
