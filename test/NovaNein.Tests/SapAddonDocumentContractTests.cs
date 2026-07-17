using NovaNein.Domain;
using NovaNein.SapAddon;

namespace NovaNein.Tests;

public sealed class SapAddonDocumentContractTests
{
    [Fact]
    public void Client_status_values_match_the_server_domain_contract()
    {
        Assert.Equal((int)DocumentStatus.NeedsReview, (int)NovaNeinDocumentStatus.NeedsReview);
        Assert.Equal((int)DocumentStatus.Rejected, (int)NovaNeinDocumentStatus.Rejected);
        Assert.Equal((int)DocumentStatus.Approved, (int)NovaNeinDocumentStatus.Approved);
        Assert.Equal((int)DocumentStatus.AttachedToSap, (int)NovaNeinDocumentStatus.AttachedToSap);
    }

    [Theory]
    [InlineData(NovaNeinDocumentStatus.NeedsReview, DocumentProgressTone.Yellow, "gelb")]
    [InlineData(NovaNeinDocumentStatus.Rejected, DocumentProgressTone.Red, "manuelle Freigabe")]
    [InlineData(NovaNeinDocumentStatus.Approved, DocumentProgressTone.Green, "freigegeben")]
    [InlineData(NovaNeinDocumentStatus.AttachedToSap, DocumentProgressTone.Green, "SAP angehängt")]
    public void Presentation_uses_the_correct_tone_and_message_for_each_business_status(NovaNeinDocumentStatus status, DocumentProgressTone expectedTone, string expectedMessagePart)
    {
        var presentation = DocumentProgressPresentation.For(status);
        Assert.Equal(expectedTone, presentation.Tone);
        Assert.Contains(expectedMessagePart, presentation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_guard_rejects_a_document_changed_since_selection()
    {
        var selected = new SapDocumentContext(SapDocumentDirection.Incoming, 10, 100, "manager");
        var active = new SapDocumentContext(SapDocumentDirection.Incoming, 11, 101, "manager");

        Assert.Equal(DocumentContextMatch.Changed, DocumentContextGuard.Compare(selected, active));
        Assert.Equal(DocumentContextMatch.Missing, DocumentContextGuard.Compare(selected, null));
        Assert.Equal(DocumentContextMatch.Current, DocumentContextGuard.Compare(selected, new SapDocumentContext(SapDocumentDirection.Incoming, 10, 100, "manager")));
    }
}
