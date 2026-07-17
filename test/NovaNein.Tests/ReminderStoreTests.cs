using Microsoft.Extensions.Configuration;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class ReminderStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "novanein-reminder-" + Guid.NewGuid().ToString("N"));
    private readonly ReminderStore _store;

    public ReminderStoreTests()
    {
        Directory.CreateDirectory(_directory);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DatabasePath"] = Path.Combine(_directory, "archive.db")
            })
            .Build();
        _store = new ReminderStore(configuration);
    }

    [Fact]
    public async Task New_workstation_gets_enabled_default_without_overwriting_choice()
    {
        await _store.InitializeAsync();
        await _store.EnsureDefaultAsync("cert-a");
        Assert.Single(await _store.EnabledAsync());

        await _store.SetEnabledAsync("cert-a", false);
        Assert.Empty(await _store.EnabledAsync());
        await _store.EnsureDefaultAsync("cert-a");
        Assert.Empty(await _store.EnabledAsync());
    }

    [Fact]
    public async Task Weekly_delivery_is_idempotent_per_recipient_and_week()
    {
        await _store.InitializeAsync();
        var week = new DateOnly(2026, 7, 6);
        Assert.True(await _store.AddWeeklyNotificationAsync(week, "cert-a", "Reminder", "Text"));
        Assert.False(await _store.AddWeeklyNotificationAsync(week, "cert-a", "Reminder", "Text"));
        Assert.Single(await _store.ListAsync("cert-a"));
        Assert.True(await _store.AddWeeklyNotificationAsync(week, "cert-b", "Reminder", "Text"));
        Assert.Single(await _store.ListAsync("cert-b"));
    }

    [Fact]
    public async Task Mark_read_is_scoped_to_the_recipient_and_persistent()
    {
        await _store.InitializeAsync();
        var week = new DateOnly(2026, 7, 6);
        Assert.True(await _store.AddWeeklyNotificationAsync(week, "cert-a", "Reminder", "Text"));
        var notification = Assert.Single(await _store.ListAsync("cert-a"));
        Assert.False(notification.IsRead);
        Assert.False(await _store.MarkReadAsync(notification.Id, "cert-b"));
        Assert.True(await _store.MarkReadAsync(notification.Id, "cert-a"));
        Assert.True(Assert.Single(await _store.ListAsync("cert-a")).IsRead);
        Assert.False(await _store.MarkReadAsync(notification.Id, "cert-a"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}
