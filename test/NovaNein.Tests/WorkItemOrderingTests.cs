using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class WorkItemOrderingTests
{
    [Fact]
    public void Sorts_sap_numbers_ascending()
    {
        var sorted = WorkItemOrdering.Apply(
            [Item(900010, "RE-9", new DateOnly(2026, 7, 3)), Item(900003, "RE-7", new DateOnly(2026, 7, 1))],
            "docNum",
            "asc").ToArray();

        Assert.Equal([900003, 900010], sorted.Select(item => item.DocNum));
    }

    [Fact]
    public void Sorts_entry_dates_descending_and_keeps_missing_dates_last()
    {
        var sorted = WorkItemOrdering.Apply(
            [Item(3, "RE-3", null), Item(2, "RE-2", new DateOnly(2026, 7, 2)), Item(1, "RE-1", new DateOnly(2026, 7, 1))],
            "entryDate",
            "desc").ToArray();

        Assert.Equal([2, 1, 3], sorted.Select(item => item.DocNum));
    }

    [Fact]
    public void Sorts_supplier_invoice_numbers_naturally()
    {
        var sorted = WorkItemOrdering.Apply(
            [Item(10, "RE-10", new DateOnly(2026, 7, 1)), Item(2, "RE-2", new DateOnly(2026, 7, 1))],
            "invoiceNumber",
            "asc").ToArray();

        Assert.Equal(["RE-2", "RE-10"], sorted.Select(item => item.InvoiceNumber));
    }

    [Theory]
    [InlineData("unknown", "asc")]
    [InlineData("docNum", "sideways")]
    public void Rejects_unsupported_sort_parameters(string field, string direction)
    {
        Assert.Throws<ArgumentException>(() => WorkItemOrdering.Validate(field, direction));
    }

    private static WorkItem Item(int docNum, string invoiceNumber, DateOnly? entryDate)
    {
        var pending = new WorkItemStage("pending", "Offen", false);
        var stages = new WorkItemStages(pending, pending, pending, pending, pending, pending, pending, pending);
        return new WorkItem(
            "incoming",
            SapDocumentKind.PurchaseInvoice.ToString(),
            docNum,
            docNum,
            invoiceNumber,
            "Lieferant",
            entryDate,
            100m,
            "EUR",
            null,
            "missing",
            "not-started",
            "not-prepared",
            "PDF hochladen",
            true,
            null,
            null,
            entryDate,
            "Eingangsrechnung",
            "pending",
            "In Bearbeitung",
            stages);
    }
}
