using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class PdfStorageCoordinatorTests
{
    [Fact]
    public async Task Cleanup_and_intake_leases_are_mutually_exclusive()
    {
        var coordinator = new PdfStorageCoordinator();
        var first = await coordinator.EnterAsync();
        var waiting = coordinator.EnterAsync().AsTask();

        await Task.Delay(20);
        Assert.False(waiting.IsCompleted);
        await first.DisposeAsync();

        await using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
