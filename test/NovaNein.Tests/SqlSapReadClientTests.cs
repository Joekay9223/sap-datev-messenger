using Microsoft.Data.SqlClient;
using NovaNein.Server;
using System.Text.RegularExpressions;

namespace NovaNein.Tests;

public sealed class SqlSapReadClientTests
{
    [Fact]
    public void Uses_only_the_four_allowlisted_document_tables_and_parameterized_doc_entry()
    {
        var expected = new Dictionary<SapDocumentKind, string>
        {
            [SapDocumentKind.PurchaseInvoice] = "OPCH",
            [SapDocumentKind.Invoice] = "OINV",
            [SapDocumentKind.PurchaseCreditNote] = "ORPC",
            [SapDocumentKind.CreditNote] = "ORIN"
        };

        Assert.Equal(
            expected.OrderBy(pair => pair.Key),
            SqlSapReadClient.AllowedDocumentTables.OrderBy(pair => pair.Key));
        foreach (var pair in expected)
        {
            var sql = SqlSapReadClient.DocumentSql(pair.Key);
            Assert.Contains($"[dbo].[{pair.Value}]", sql, StringComparison.Ordinal);
            Assert.Contains("WHERE d.[DocEntry] = @docEntry", sql, StringComparison.Ordinal);
            Assert.Contains("d.[Comments]", sql, StringComparison.Ordinal);
            AssertReadOnly(sql);
        }
    }

    [Fact]
    public void Maps_supplier_reference_for_incoming_and_doc_num_for_outgoing_documents()
    {
        Assert.Equal("RE-4711", SqlSapReadClient.InvoiceNumber(SapDocumentKind.PurchaseInvoice, 99, "RE-4711"));
        Assert.Equal("GS-42", SqlSapReadClient.InvoiceNumber(SapDocumentKind.PurchaseCreditNote, 99, "GS-42"));
        Assert.Equal("99", SqlSapReadClient.InvoiceNumber(SapDocumentKind.Invoice, 99, "customer-reference"));
        Assert.Equal("99", SqlSapReadClient.InvoiceNumber(SapDocumentKind.CreditNote, 99, "customer-reference"));
    }

    [Fact]
    public void Missing_pdf_scan_uses_create_date_and_checks_atc1_for_a_pdf()
    {
        var sql = SqlSapReadClient.MissingPdfSql;

        Assert.DoesNotContain("DocDate", sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(Count(sql, "[CreateDate]") >= 12);
        Assert.Equal(4, Count(sql, "[dbo].[ATC1]"));
        Assert.Equal(4, Count(sql, "[FileExt]"));
        Assert.Contains("@fromEntryDate", sql, StringComparison.Ordinal);
        Assert.Contains("@toEntryDate", sql, StringComparison.Ordinal);
        foreach (var table in SqlSapReadClient.AllowedDocumentTables.Values)
            Assert.Contains($"[dbo].[{table}]", sql, StringComparison.Ordinal);
        AssertReadOnly(sql);
    }

    [Fact]
    public void Accounting_header_uses_sap_card_code_as_datev_person_account_not_control_account()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "NovaNein.Server", "SqlSapReadClient.cs"));
        Assert.Contains("d.[CardCode] AS [PartnerAccountNumber]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("bp.[DebPayAcct] AS [PartnerAccountNumber]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Accounting_header_prefers_bill_to_address_for_datev_supplier_data()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "NovaNein.Server", "SqlSapReadClient.cs"));
        Assert.Contains("CASE WHEN a.[AdresType] = 'B' THEN 0 WHEN a.[AdresType] = 'S' THEN 1 ELSE 2 END", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Forces_application_intent_read_only_even_when_configuration_omits_it()
    {
        var value = SqlSapReadClient.BuildReadOnlyConnectionString(
            "Server=sql.test;Database=EXAMPLE;Integrated Security=true;Encrypt=true");
        var parsed = new SqlConnectionStringBuilder(value);

        Assert.Equal(ApplicationIntent.ReadOnly, parsed.ApplicationIntent);
        Assert.Equal("NovaNein SQL read-only", parsed.ApplicationName);
    }

    [Fact]
    public void Readiness_probe_is_a_fixed_select()
    {
        AssertReadOnly(SqlSapReadClient.ReadinessSql);
        Assert.Contains("[dbo].[OINV]", SqlSapReadClient.ReadinessSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Supplier_matching_and_coding_history_are_fixed_parameterized_read_only_queries()
    {
        AssertReadOnly(SqlSapReadClient.SupplierMatchingSql);
        Assert.Contains("[dbo].[OCRD]", SqlSapReadClient.SupplierMatchingSql, StringComparison.Ordinal);
        Assert.Contains("[dbo].[OCRB]", SqlSapReadClient.SupplierMatchingSql, StringComparison.Ordinal);
        Assert.Contains("[dbo].[CRD1]", SqlSapReadClient.SupplierMatchingSql, StringComparison.Ordinal);

        AssertReadOnly(SqlSapReadClient.SupplierCodingHistorySql);
        Assert.Contains("[dbo].[OPCH]", SqlSapReadClient.SupplierCodingHistorySql, StringComparison.Ordinal);
        Assert.Contains("[dbo].[PCH1]", SqlSapReadClient.SupplierCodingHistorySql, StringComparison.Ordinal);
        Assert.Contains("@cardCode", SqlSapReadClient.SupplierCodingHistorySql, StringComparison.Ordinal);

        AssertReadOnly(SqlSapReadClient.PurchaseInvoiceDuplicateSql);
        Assert.Contains("[dbo].[OPCH]", SqlSapReadClient.PurchaseInvoiceDuplicateSql, StringComparison.Ordinal);
        Assert.Contains("@cardCode", SqlSapReadClient.PurchaseInvoiceDuplicateSql, StringComparison.Ordinal);
        Assert.Contains("@invoiceNumber", SqlSapReadClient.PurchaseInvoiceDuplicateSql, StringComparison.Ordinal);
        Assert.Contains("[CANCELED]", SqlSapReadClient.PurchaseInvoiceDuplicateSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_validation_reads_name_and_posting_state_from_oact_with_a_parameter()
    {
        var sql = SqlSapReadClient.AccountValidationSql;

        AssertReadOnly(sql);
        Assert.Contains("[dbo].[OACT]", sql, StringComparison.Ordinal);
        Assert.Contains("[AcctCode]", sql, StringComparison.Ordinal);
        Assert.Contains("[AcctName]", sql, StringComparison.Ordinal);
        Assert.Contains("[Postable]", sql, StringComparison.Ordinal);
        Assert.Contains("@account", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Tax_breakdown_uses_the_official_sap_b1_stc_code_column()
    {
        foreach (var pair in SqlSapReadClient.TaxTables)
        {
            var sql = SqlSapReadClient.TaxBreakdownSql(pair.Key);
            Assert.Contains($"[dbo].[{pair.Value}]", sql, StringComparison.Ordinal);
			Assert.Contains("ISNULL(t.[StcCode], '') AS [TaxCode]", sql, StringComparison.Ordinal);
			Assert.Contains("WHERE t.[DocEntry] = @docEntry", sql, StringComparison.Ordinal);
			Assert.Contains("[TaxSumFrgn]", sql, StringComparison.Ordinal);
			Assert.Contains("[BaseSumFrg]", sql, StringComparison.Ordinal);
			Assert.Contains("[RvsChrgPrc]", sql, StringComparison.Ordinal);
			Assert.Contains("[RvsChrgTax]", sql, StringComparison.Ordinal);
			Assert.Contains("[InGrossRev]", sql, StringComparison.Ordinal);
            AssertReadOnly(sql);
        }
    }

	[Fact]
	public void Applies_sap_reverse_charge_tax_metadata_to_matching_lines()
	{
		var lines = new[] { new SapDocumentLine(0, "MSC", 1m, 378m, 71.82m, "E13", 19m, "5800", "EUR") };
		var taxes = new[] { new SapTaxBreakdown(0, "E13", 19m, 378m, 71.82m, "EUR", ReverseChargePercent: 100m, ReverseChargeTaxAmount: 71.82m) };

		var enriched = SqlSapReadClient.ApplyTaxMetadata(lines, taxes);

		Assert.True(enriched[0].IsReverseCharge);
	}

    [Fact]
    public void Sap_avt1_is_the_primary_datev_bu_code_source()
    {
        var date = new DateOnly(2026, 7, 1);
        var fromSap = SqlSapReadClient.ResolveSapAvt1Mapping("V2", "9", date, null);

        Assert.NotNull(fromSap);
        Assert.Equal("V2", fromSap!.SapTaxCode);
        Assert.Equal("9", fromSap.DatevBuCode);
        Assert.Equal("SAP AVT1", fromSap.ApprovedBy);
        Assert.NotEmpty(fromSap.MappingHash);

        var contradictoryLocalMapping = fromSap with { DatevBuCode = "0", ApprovedBy = "Altbestand" };
        Assert.Null(SqlSapReadClient.ResolveSapAvt1Mapping("V2", "9", date, contradictoryLocalMapping));

		var explicitlyApprovedHistoricFallback = fromSap with { SapTaxCode = "A7", DatevBuCode = "11", ApprovedBy = "Buchhaltung" };
		Assert.Same(explicitlyApprovedHistoricFallback, SqlSapReadClient.ResolveSapAvt1Mapping("A7", null, date, explicitlyApprovedHistoricFallback));
    }

    [Fact]
    public void Avt1_lookup_uses_the_latest_effective_mapping_for_the_document_date()
    {
        var sql = SqlSapReadClient.Avt1MappingSql;
        Assert.Contains("[EffecDate] <= @documentDate", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY [EffecDate] DESC, [LogInstanc] DESC", sql, StringComparison.Ordinal);
        AssertReadOnly(sql);
    }

    [Fact]
    public void Builds_localization_independent_tax_breakdown_from_sap_document_lines()
    {
        var lines = new[]
        {
            new SapDocumentLine(0, "Ware", 1m, 100m, 19m, "V2", 19m, "4930", "EUR"),
            new SapDocumentLine(1, "Fracht", 1m, 20m, 3.8m, "v2", 19m, "4730", "EUR")
        };

        var tax = Assert.Single(SqlSapReadClient.BuildTaxBreakdownFromLines(lines));
        Assert.Equal("V2", tax.TaxCode);
        Assert.Equal(120m, tax.NetAmount);
        Assert.Equal(22.8m, tax.TaxAmount);
        Assert.Equal(19m, tax.Rate);
    }

	[Fact]
	public void Uses_document_currency_amounts_in_header_list_lines_and_tax_queries()
	{
		var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "NovaNein.Server", "SqlSapReadClient.cs"));
		Assert.Contains("[DocTotalFC]", SqlSapReadClient.DocumentSql(SapDocumentKind.Invoice), StringComparison.Ordinal);
		Assert.Contains("[DocTotalFC]", SqlSapReadClient.ListDocumentsSql(SapDocumentKind.Invoice), StringComparison.Ordinal);
		Assert.Contains("[TotalFrgn]", source, StringComparison.Ordinal);
		Assert.Contains("[VatSumFrgn]", source, StringComparison.Ordinal);
		Assert.Contains("[TaxSumFrgn]", source, StringComparison.Ordinal);
		Assert.Contains("[BaseSumFrg]", source, StringComparison.Ordinal);
	}

    private static void AssertReadOnly(string sql)
    {
        var normalized = sql.TrimStart();
        Assert.StartsWith("SELECT", normalized, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC", "DROP", "ALTER", "CREATE", "TRUNCATE" })
            Assert.False(Regex.IsMatch(normalized, $@"\b{Regex.Escape(forbidden)}\b", RegexOptions.IgnoreCase), $"SQL enthält das verbotene Schlüsselwort {forbidden}.");
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "NovaNein.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository-Wurzel nicht gefunden.");
    }
}
