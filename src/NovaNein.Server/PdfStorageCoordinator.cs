namespace NovaNein.Server;

/// <summary>
/// Serialisiert die kurze Dateispeicher-/Datenbank-Aufnahme mit der Orphan-Bereinigung.
/// Damit kann eine alte unreferenzierte Hashdatei nicht genau während ihrer erneuten
/// Verwendung durch einen neuen Intake gelöscht werden.
/// </summary>
public sealed class PdfStorageCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
