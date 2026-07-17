using NovaNein.Domain;

namespace NovaNein.Tests;

/// <summary>
/// Repeats the invoice comparison contract over a deterministic 100-case corpus.
/// The corpus is intentionally synthetic; live SAP/PDF evidence is tracked separately.
/// </summary>
public sealed class InvoiceValidationCorpusTests
{
    public static IEnumerable<object[]> Cases()
    {
        for (var index = 0; index < 100; index++)
        {
            var expected = index % 2 == 0 ? ReviewSignal.Green : ReviewSignal.Red;
            var invoiceNumber = index % 2 == 0
                ? (index % 4 == 0 ? "RE / 00017" : "RE-000017")
                : $"RE-{index + 18:000000}";
            var amount = index % 2 == 0 ? 119m : 119m + index;
            yield return [index, invoiceNumber, amount, expected];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compares_invoice_corpus_case(int index, string invoiceNumber, decimal amount, ReviewSignal expected)
    {
        var sap = new InvoiceFacts(
            "RE-00017",
            "Example Supplier GmbH",
            "DE000000001",
            119m,
            "EUR",
            new DateOnly(2026, 7, 10),
            true,
            false);
        var pdf = sap with { InvoiceNumber = invoiceNumber, GrossAmount = amount };

        Assert.Equal(expected, InvoiceValidation.Compare(sap, pdf).Signal);
        Assert.True(index is >= 0 and < 100);
    }
}
