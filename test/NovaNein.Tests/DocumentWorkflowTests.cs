using NovaNein.Domain;

namespace NovaNein.Tests;

public sealed class DocumentWorkflowTests
{
    [Theory]
    [InlineData(DocumentStatus.Approved, ReviewSignal.Green)]
    [InlineData(DocumentStatus.Approved, ReviewSignal.Yellow)]
    [InlineData(DocumentStatus.AttachedToSap, ReviewSignal.Green)]
    [InlineData(DocumentStatus.AttachedToSap, ReviewSignal.Yellow)]
    [InlineData(DocumentStatus.Approved, ReviewSignal.Red)]
    [InlineData(DocumentStatus.AttachedToSap, ReviewSignal.Red)]
    public void Allows_datev_package_after_approval_without_sap_attachment(DocumentStatus status, ReviewSignal signal)
    {
        var document = new DocumentRecord(Guid.NewGuid(), new(DocumentDirection.Incoming, 1, 2), "A".PadLeft(64, 'A'), "invoice.pdf", status, signal, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.True(DocumentWorkflow.MayCreateDatevPackage(document));
    }

    [Theory]
    [InlineData(DocumentStatus.NeedsReview, ReviewSignal.Yellow)]
    [InlineData(DocumentStatus.Rejected, ReviewSignal.Red)]
    [InlineData(DocumentStatus.Failed, null)]
    public void Blocks_datev_package_before_approval(DocumentStatus status, ReviewSignal? signal)
    {
        var document = new DocumentRecord(Guid.NewGuid(), new(DocumentDirection.Incoming, 1, 2), "B".PadLeft(64, 'B'), "invoice.pdf", status, signal, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.False(DocumentWorkflow.MayCreateDatevPackage(document));
    }

    [Fact]
    public void Allows_direct_transition_from_approved_to_packaged()
    {
        Assert.True(DocumentWorkflow.CanTransition(DocumentStatus.Approved, DocumentStatus.Packaged));
    }

    [Fact]
    public void Allows_reasoned_manual_approval_after_a_red_validation()
    {
        Assert.True(DocumentWorkflow.CanTransition(DocumentStatus.Rejected, DocumentStatus.Approved));
    }
}
