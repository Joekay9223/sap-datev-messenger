using Microsoft.Extensions.Configuration;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class WorkstationRegistryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-workstations-{Guid.NewGuid():N}");
    private WorkstationRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        _registry = new WorkstationRegistry(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DatabasePath"] = Path.Combine(_directory, "archive.db")
        }).Build());
        await _registry.InitializeAsync();
    }

    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }

    [Fact]
    public async Task Accepts_only_registered_normalized_thumbprints()
    {
        const string thumbprint = "abcdef0123456789abcdef0123456789abcdef01";
        await _registry.RegisterAsync("ab cd ef 01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01", "AP-01");
        Assert.True(await _registry.IsRegisteredAsync(thumbprint.ToUpperInvariant()));
        Assert.False(await _registry.IsRegisteredAsync("1234567890123456789012345678901234567890"));
    }

    [Fact]
    public void Rejects_invalid_thumbprints()
    {
        Assert.Throws<ArgumentException>(() => WorkstationRegistry.NormalizeThumbprint("not a certificate"));
    }

    [Fact]
    public async Task Revoked_workstation_cannot_authenticate_again()
    {
        const string thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
        await _registry.RegisterAsync(thumbprint, "AP-02");
        Assert.True(await _registry.RevokeAsync(thumbprint));
        Assert.False(await _registry.IsRegisteredAsync(thumbprint));
        Assert.False(await _registry.RevokeAsync(thumbprint));
    }

    [Fact]
    public async Task Re_registering_an_active_workstation_is_idempotent()
    {
        const string thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";
        await _registry.RegisterAsync(thumbprint, "AP-03");
        await _registry.RegisterAsync(thumbprint, "AP-03");
        Assert.True(await _registry.IsRegisteredAsync(thumbprint));
    }

    [Fact]
    public async Task Rotating_a_workstation_certificate_replaces_the_old_certificate()
    {
        const string oldThumbprint = "0123456789ABCDEF0123456789ABCDEF01234568";
        const string newThumbprint = "0123456789ABCDEF0123456789ABCDEF01234569";

        await _registry.RegisterAsync(oldThumbprint, "AP-04");
        await _registry.RegisterAsync(newThumbprint, "AP-04");

        Assert.False(await _registry.IsRegisteredAsync(oldThumbprint));
        Assert.True(await _registry.IsRegisteredAsync(newThumbprint));
    }

    [Fact]
    public async Task Records_latest_health_and_ordered_history_for_registered_workstation()
    {
        const string thumbprint = "1123456789ABCDEF0123456789ABCDEF01234567";
        await _registry.RegisterAsync(thumbprint, "SAP-SERVER");

        await _registry.RecordHealthAsync(thumbprint, new("1.1.0", "sap-addon", "ok", "erste Prüfung"));
        await _registry.RecordHealthAsync(thumbprint, new("1.1.0", "sap-addon", "degraded", "zweite Prüfung"));
        var history = await _registry.HealthHistoryAsync(thumbprint);

        Assert.Equal(2, history.Count);
        Assert.Equal("zweite Prüfung", history[0].Detail);
        Assert.Equal("SAP-SERVER", history[0].WorkstationName);
        Assert.Equal("erste Prüfung", history[1].Detail);
    }

    [Fact]
    public async Task Refuses_health_from_unregistered_workstation()
    {
        const string thumbprint = "2123456789ABCDEF0123456789ABCDEF01234567";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _registry.RecordHealthAsync(thumbprint, new("1.1.0", "sap-addon", "ok", "")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Refuses_unbounded_health_history_limit(int limit)
    {
        const string thumbprint = "3123456789ABCDEF0123456789ABCDEF01234567";

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _registry.HealthHistoryAsync(thumbprint, limit));
    }

    [Fact]
    public async Task Keeps_only_latest_two_hundred_health_events_per_workstation()
    {
        const string thumbprint = "4123456789ABCDEF0123456789ABCDEF01234567";
        await _registry.RegisterAsync(thumbprint, "SAP-STRESSTEST");

        for (var index = 0; index < 205; index++)
            await _registry.RecordHealthAsync(thumbprint, new("1.1.0", "sap-addon", "ok", $"Ereignis {index}"));

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_directory, "archive.db")}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), (
              SELECT detail FROM workstation_health_events
              WHERE certificate_thumbprint=$thumbprint
              ORDER BY occurred_at ASC, id ASC LIMIT 1
            )
            FROM workstation_health_events
            WHERE certificate_thumbprint=$thumbprint;
            """;
        command.Parameters.AddWithValue("$thumbprint", thumbprint);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(200, reader.GetInt32(0));
        Assert.Equal("Ereignis 5", reader.GetString(1));
    }
}
