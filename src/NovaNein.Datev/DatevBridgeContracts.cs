using NovaNein.Domain;
using System.Text.Json;

namespace NovaNein.Datev;

public sealed record DatevBridgeManifest(
    int Version,
    Guid RequestId,
    Guid DocumentId,
    DocumentDirection Direction,
    int DocNum,
    string PackageFileName,
    string PackageSha256,
    DateTimeOffset CreatedAt);

public sealed record DatevBridgeReceipt(
    int Version,
    Guid RequestId,
    Guid DocumentId,
    string PackageFileName,
    string PackageSha256,
    bool Succeeded,
    int Attempts,
    DateTimeOffset OccurredAt,
    string? Error);

public sealed record DatevBridgeHeartbeat(
    int Version,
    DateTimeOffset OccurredAt,
    string Status,
    string? Error);

public static class DatevBridgeJson
{
    public static JsonSerializerOptions SerializerOptions { get; } =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
