using System.Text.Json;
using NovaNein.SapAddon;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class ClientHealthTests
{
    [Fact]
    public void Normalizes_bounded_health_report()
    {
        var report = ClientHealthRules.Normalize(new(" 1.1.0 ", " sap-addon ", "OK", " SAP UI verbunden "));

        Assert.Equal("1.1.0", report.ClientVersion);
        Assert.Equal("sap-addon", report.ClientKind);
        Assert.Equal("ok", report.Status);
        Assert.Equal("SAP UI verbunden", report.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("healthy")]
    public void Rejects_unknown_health_status(string status)
    {
        Assert.Throws<ArgumentException>(() => ClientHealthRules.Normalize(new("1.1.0", "sap-addon", status, "")));
    }

    [Fact]
    public void Bounds_health_detail_without_rejecting_heartbeat()
    {
        var report = ClientHealthRules.Normalize(new("1.1.0", "sap-addon", "degraded", new string('x', 900)));

        Assert.Equal(ClientHealthRules.MaximumDetailLength, report.Detail!.Length);
    }

    [Theory]
    [InlineData("1.1.0", "1.1.0", true)]
    [InlineData("1.2.0", "1.1.0", true)]
    [InlineData("2.0.0", "1.1.0", false)]
    [InlineData("1.0.9", "1.1.0", false)]
    [InlineData("invalid", "1.1.0", false)]
    [InlineData("1.1.0", "invalid", false)]
    public void Calculates_minimum_client_compatibility(string client, string minimum, bool expected)
    {
        Assert.Equal(expected, ClientHealthRules.IsCompatible(client, minimum));
    }

    [Fact]
    public void Serializes_health_report_with_explicit_contract()
    {
        var json = NovaNeinClientJson.SerializeHealthReport("1.1.0", "sap-addon", "ok", "SAP UI verbunden");
        using var document = JsonDocument.Parse(json);

        Assert.Equal("1.1.0", document.RootElement.GetProperty("clientVersion").GetString());
        Assert.Equal("sap-addon", document.RootElement.GetProperty("clientKind").GetString());
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Parses_complete_compatible_health_response()
    {
        var response = NovaNeinClientJson.ParseHealthResponse("""
            {"serverVersion":"1.1.0","compatible":true,"checkedAt":"2026-07-11T20:00:00Z","workstation":{"workstationName":"SAP-SERVER","clientVersion":"1.1.0","clientKind":"sap-addon","status":"ok","detail":"SAP UI verbunden"}}
            """);

        Assert.True(response.Compatible);
        Assert.Equal("SAP-SERVER", response.Workstation.WorkstationName);
        Assert.Equal(new DateTimeOffset(2026, 7, 11, 20, 0, 0, TimeSpan.Zero), response.CheckedAt);
    }

    [Fact]
    public void Parses_non_negative_statistics_response()
    {
        var statistics = NovaNeinClientJson.ParseStatisticsResponse("""
            {"total":10,"received":1,"needsReview":2,"approved":3,"rejected":1,"failed":1,"attachedToSap":2,"createdLast7Days":4,"createdLast30Days":9}
            """);

        Assert.Equal(10, statistics.Total);
        Assert.Equal(2, statistics.NeedsReview);
        Assert.Equal(2, statistics.AttachedToSap);
    }

    [Theory]
    [InlineData(0, 60000)]
    [InlineData(1, 5000)]
    [InlineData(2, 10000)]
    [InlineData(3, 20000)]
    [InlineData(4, 40000)]
    [InlineData(5, 60000)]
    [InlineData(20, 60000)]
    public void Uses_bounded_reconnect_backoff(int failures, int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, NovaNeinHealthRetryPolicy.NextDelayMilliseconds(failures));
    }
}
