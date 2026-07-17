using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class WorkItemUploadTargetsTests
{
    [Fact]
    public void Includes_all_assignable_documents_independent_of_visible_page()
    {
        var items = Enumerable.Range(910758, 60)
            .Select(docNum => Item(docNum, "missing", null))
            .Append(Item(900003, "linked", Guid.NewGuid()))
            .ToArray();

        var targets = WorkItemUploadTargets.Select(items).ToArray();

        Assert.Equal(60, targets.Length);
        Assert.Equal(910758, targets[0].DocNum);
        Assert.Contains(targets, item => item.DocNum == 910758 && item.DocEntry == 70600);
        Assert.DoesNotContain(targets, item => item.DocNum == 900003);
    }

    [Fact]
    public void Includes_sap_only_document_without_archived_pdf()
    {
        var target = Assert.Single(WorkItemUploadTargets.Select([Item(900002, "sap-only", null)]));

        Assert.Equal(900002, target.DocNum);
    }

    [Fact]
    public void Excludes_ignored_documents_from_upload_targets()
    {
        var ignored = Item(900394, "missing", null) with
        {
            Ignored = true,
            IgnoredReason = "Beleg wurde storniert",
            OverallState = "ignored",
            OverallLabel = "Ignoriert"
        };

        Assert.Empty(WorkItemUploadTargets.Select([ignored]));
    }

    private static WorkItem Item(int docNum, string pdfState, Guid? documentId)
    {
        var pending = new WorkItemStage("pending", "Offen", false);
        var stages = new WorkItemStages(pending, pending, pending, pending, pending, pending, pending, pending);
        return new WorkItem(
            "outgoing",
            SapDocumentKind.Invoice.ToString(),
            docNum == 910758 ? 70600 : docNum,
            docNum,
            docNum.ToString(),
            "Geschäftspartner",
            new DateOnly(2026, 7, 1),
            100m,
            "EUR",
            documentId,
            pdfState,
            "not-started",
            "not-prepared",
            "PDF hochladen",
            true,
            null,
            null,
            new DateOnly(2026, 7, 1),
            "Ausgangsrechnung",
            "pending",
            "In Bearbeitung",
            stages);
    }
}
