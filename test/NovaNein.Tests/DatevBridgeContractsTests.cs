using System.Text.Json;
using NovaNein.Datev;

namespace NovaNein.Tests;

public sealed class DatevBridgeContractsTests
{
    [Fact]
    public void Heartbeat_written_by_bridge_is_readable_by_server()
    {
        var occurredAt = DateTimeOffset.Parse("2026-07-13T12:12:48.9585565+00:00");
        var json = JsonSerializer.Serialize(
            new DatevBridgeHeartbeat(1, occurredAt, "ready", null),
            DatevBridgeJson.SerializerOptions);

        Assert.Contains("\"occurredAt\"", json);
        var heartbeat = JsonSerializer.Deserialize<DatevBridgeHeartbeat>(json, DatevBridgeJson.SerializerOptions);

        Assert.NotNull(heartbeat);
        Assert.Equal(1, heartbeat.Version);
        Assert.Equal(occurredAt, heartbeat.OccurredAt);
        Assert.Equal("ready", heartbeat.Status);
    }
}
