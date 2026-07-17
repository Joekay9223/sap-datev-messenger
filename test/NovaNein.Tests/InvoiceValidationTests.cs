using NovaNein.Domain;

namespace NovaNein.Tests;

public class InvoiceValidationTests
{
    private static readonly InvoiceFacts Sap = new("RE-00017", "Example Supplier GmbH", "DE000000001", 119m, "EUR", new DateOnly(2026, 7, 10), true, false);

    [Fact]
    public void Marks_normalized_exact_match_green()
    {
        var pdf = Sap with { InvoiceNumber = "RE 17", BusinessPartnerName = "Mueller Soehne GmbH" };
        var result = InvoiceValidation.Compare(Sap, pdf);
        Assert.Equal(ReviewSignal.Green, result.Signal);
    }

    [Theory]
    [InlineData("910809", "RE-EXAMPLE-009")]
    [InlineData("910809", "RG-910809")]
    [InlineData("910809", "INV / EXAMPLE-009")]
    [InlineData("910809", "Rechnung Nr. 910809")]
    [InlineData("RE-00017", "17")]
    public void Treats_common_invoice_prefixes_as_equivalent(string sapNumber, string pdfNumber)
    {
        var sap = Sap with { InvoiceNumber = sapNumber };
        var pdf = sap with { InvoiceNumber = pdfNumber };

        Assert.True(InvoiceValidation.InvoiceNumbersMatch(sapNumber, pdfNumber));
        Assert.Equal(ReviewSignal.Green, InvoiceValidation.Compare(sap, pdf).Signal);
    }

    [Theory]
    [InlineData("R-EXAMPLE-001", "R-26-13S40")]
    [InlineData("INV-2026-1008", "INV-2O26-1O08")]
    public void Treats_limited_common_ocr_character_confusions_as_equivalent(string sapNumber, string pdfNumber)
    {
        var sap = Sap with { InvoiceNumber = sapNumber };
        var pdf = sap with { InvoiceNumber = pdfNumber };

        Assert.True(InvoiceValidation.InvoiceNumbersMatch(sapNumber, pdfNumber));
        Assert.Equal(ReviewSignal.Green, InvoiceValidation.Compare(sap, pdf).Signal);
    }

    [Theory]
    [InlineData("910809", "RE-EXAMPLE-008")]
    [InlineData("910809", "RE2026-910809")]
    [InlineData("INV261005", "INV261006")]
    public void Keeps_different_invoice_numbers_red(string sapNumber, string pdfNumber)
    {
        var sap = Sap with { InvoiceNumber = sapNumber };
        var pdf = sap with { InvoiceNumber = pdfNumber };

        Assert.False(InvoiceValidation.InvoiceNumbersMatch(sapNumber, pdfNumber));
        Assert.Equal(ReviewSignal.Red, InvoiceValidation.Compare(sap, pdf).Signal);
    }

    [Fact]
    public void Marks_ocr_uncertainty_yellow()
    {
        var pdf = Sap with { IsDocumentQualityUncertain = true };
        var result = InvoiceValidation.Compare(Sap, pdf);
        Assert.Equal(ReviewSignal.Yellow, result.Signal);
        Assert.True(result.RequiresManualReview);
    }

    [Theory]
    [InlineData("RE-00018", 119, "EUR")]
    [InlineData("RE-00017", 120, "EUR")]
    [InlineData("RE-00017", 119, "USD")]
    public void Marks_hard_mismatches_red(string invoiceNumber, decimal amount, string currency)
    {
        var pdf = Sap with { InvoiceNumber = invoiceNumber, GrossAmount = amount, Currency = currency };
        var result = InvoiceValidation.Compare(Sap, pdf);
        Assert.Equal(ReviewSignal.Red, result.Signal);
        Assert.True(result.RequiresManualReview);
    }

    [Fact]
    public void Marks_large_date_deviation_red()
    {
        var pdf = Sap with { InvoiceDate = new DateOnly(2026, 9, 1) };
        Assert.Equal(ReviewSignal.Red, InvoiceValidation.Compare(Sap, pdf).Signal);
    }

    [Fact]
    public void Sends_image_only_pdf_to_manual_review_instead_of_rejecting_it()
    {
        var pdf = new InvoiceFacts("", "", null, 0m, "", DateOnly.MinValue, false, false, true, false);

        var result = InvoiceValidation.Compare(Sap, pdf);

        Assert.Equal(ReviewSignal.Yellow, result.Signal);
        Assert.Single(result.Reasons);
        Assert.Contains("durch OpenAI nicht zuverlässig gelesen", result.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_unreadable_ocr_fields_yellow_but_explicit_mismatches_red()
    {
        var unreadable = Sap with { GrossAmount = 0m, Currency = "", IsDocumentQualityUncertain = true };
        var explicitMismatch = Sap with { GrossAmount = 118m, IsDocumentQualityUncertain = true };

        Assert.Equal(ReviewSignal.Yellow, InvoiceValidation.Compare(Sap, unreadable).Signal);
        Assert.Equal(ReviewSignal.Red, InvoiceValidation.Compare(Sap, explicitMismatch).Signal);
    }
}
