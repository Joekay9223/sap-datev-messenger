using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class WorkItemIgnoreStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "novanein-ignore-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly WorkItemIgnoreStore _store;

    public WorkItemIgnoreStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "archive.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DatabasePath"] = _databasePath
            })
            .Build();
        _store = new WorkItemIgnoreStore(configuration);
    }

    [Fact]
    public async Task Ignore_is_persistent_and_restore_keeps_an_audit_trail()
    {
        await _store.InitializeAsync();

        var ignored = await _store.IgnoreAsync(
            SapDocumentKind.PurchaseInvoice,
            90001,
            900001,
            "Beleg wurde in SAP storniert",
            "john-admin");

        Assert.Equal(90001, ignored.DocEntry);
        var active = await _store.ListActiveAsync();
        var persisted = Assert.Single(active).Value;
        Assert.Equal("Beleg wurde in SAP storniert", persisted.Reason);
        Assert.Equal("john-admin", persisted.IgnoredBy);

        Assert.True(await _store.RestoreAsync(
            SapDocumentKind.PurchaseInvoice,
            90001,
            900001,
            "Storno wurde zurückgenommen",
            "john-admin"));
        Assert.Empty(await _store.ListActiveAsync());
        Assert.False(await _store.RestoreAsync(
            SapDocumentKind.PurchaseInvoice,
            90001,
            900001,
            "Bereits wieder geöffnet",
            "john-admin"));

        await using var connection = new SqliteConnection("Data Source=" + _databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT action,reason FROM work_item_ignore_audit ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Ignored", reader.GetString(0));
        Assert.Equal("Beleg wurde in SAP storniert", reader.GetString(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Restored", reader.GetString(0));
        Assert.Equal("Storno wurde zurückgenommen", reader.GetString(1));
        Assert.False(await reader.ReadAsync());

        var history = await _store.HistoryAsync(SapDocumentKind.PurchaseInvoice, 90001);
        Assert.Collection(history,
            entry =>
            {
                Assert.Equal("Ignored", entry.Action);
                Assert.Equal("Beleg wurde in SAP storniert", entry.Reason);
                Assert.Equal("john-admin", entry.Actor);
            },
            entry =>
            {
                Assert.Equal("Restored", entry.Action);
                Assert.Equal("Storno wurde zurückgenommen", entry.Reason);
            });
    }

    [Fact]
    public async Task Reignore_updates_the_active_reason_without_losing_history()
    {
        await _store.InitializeAsync();
        await _store.IgnoreAsync(SapDocumentKind.Invoice, 42, 100042, "Erste Begründung", "admin");
        await _store.RestoreAsync(SapDocumentKind.Invoice, 42, 100042, "Wieder öffnen", "admin");
        await _store.IgnoreAsync(SapDocumentKind.Invoice, 42, 100042, "Erneut storniert", "admin");

        var current = Assert.Single(await _store.ListActiveAsync()).Value;
        Assert.Equal("Erneut storniert", current.Reason);

        await using var connection = new SqliteConnection("Data Source=" + _databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM work_item_ignore_audit";
        Assert.Equal(3L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(0, "gültige Begründung")]
    [InlineData(1, "x")]
    public async Task Rejects_invalid_ignore_data(int docEntry, string reason)
    {
        await _store.InitializeAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _store.IgnoreAsync(
            SapDocumentKind.Invoice,
            docEntry,
            100,
            reason,
            "admin"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}
