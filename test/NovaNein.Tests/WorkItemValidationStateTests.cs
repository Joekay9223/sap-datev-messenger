using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class WorkItemValidationStateTests
{
    [Theory]
    [InlineData(ReviewSignal.Green)]
    [InlineData(ReviewSignal.Yellow)]
    [InlineData(ReviewSignal.Red)]
    public void Preserves_completed_validation_when_a_later_processing_step_fails(ReviewSignal signal)
    {
        Assert.Equal("approved", WorkItemService.ValidationStateFor(DocumentStatus.Failed, signal));
    }

    [Fact]
    public void Keeps_a_technical_validation_failure_failed_without_a_validation_result()
    {
        Assert.Equal("failed", WorkItemService.ValidationStateFor(DocumentStatus.Failed));
    }

    [Fact]
    public void Offers_package_retry_even_when_the_document_has_a_technical_error()
    {
        Assert.Equal(
            "DATEV-Paket erneut vorbereiten",
            WorkItemService.NextAction("linked", "approved", "package-failed", supported: true, "Die Verarbeitung ist fehlgeschlagen."));
    }

	[Fact]
	public void Offers_an_explicit_datev_release_for_an_approved_credit_note()
	{
		Assert.Equal(
			"Gutschrift für DATEV freigeben",
			WorkItemService.NextAction("linked", "approved", "not-prepared", supported: true, error: null, creditNote: true));
	}

	[Fact]
	public void Counts_an_approved_credit_note_awaiting_release_as_ready_for_datev()
	{
		var complete = new WorkItemStage("complete", "Erledigt", true);
		var pending = new WorkItemStage("pending", "Offen", false);
		var stages = new WorkItemStages(complete, complete, pending, complete, pending, pending, pending, pending);
		var item = new WorkItem(
			"incoming",
			SapDocumentKind.PurchaseCreditNote.ToString(),
			517,
			319516,
			"TS 1100152-0008",
			"Ergo Versicherungs AG",
			new DateOnly(2026, 7, 9),
			100m,
			"EUR",
			Guid.NewGuid(),
			"linked",
			"approved",
			"not-prepared",
			"Gutschrift für DATEV freigeben",
			true,
			null,
			null,
			new DateOnly(2026, 7, 14),
			"Eingangsgutschrift",
			"pending",
			"In Bearbeitung",
			stages,
			[new WorkItemAction("credit-note-release", "Gutschrift für DATEV freigeben")]);

		Assert.True(WorkItemService.IsReadyForDatev(item));
	}
}
