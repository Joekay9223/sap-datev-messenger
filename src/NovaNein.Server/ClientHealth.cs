namespace NovaNein.Server;

public sealed record ClientHealthReport(
    string ClientVersion,
    string ClientKind,
    string Status,
    string? Detail);

public sealed record WorkstationHealthSnapshot(
    string CertificateThumbprint,
    string WorkstationName,
    string ClientVersion,
    string ClientKind,
    string Status,
    string Detail,
    DateTimeOffset LastSeenAt);

public static class ClientHealthRules
{
    public const int MaximumVersionLength = 32;
    public const int MaximumKindLength = 32;
    public const int MaximumDetailLength = 500;

    private static readonly HashSet<string> AllowedStatuses =
        new(StringComparer.Ordinal) { "ok", "degraded", "error" };

    public static ClientHealthReport Normalize(ClientHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var version = RequireBounded(report.ClientVersion, nameof(report.ClientVersion), MaximumVersionLength);
        var kind = RequireBounded(report.ClientKind, nameof(report.ClientKind), MaximumKindLength);
        var status = RequireBounded(report.Status, nameof(report.Status), 16).ToLowerInvariant();
        if (!AllowedStatuses.Contains(status))
            throw new ArgumentException("Der Health-Status muss ok, degraded oder error sein.", nameof(report));
        var detail = (report.Detail ?? string.Empty).Trim();
        if (detail.Length > MaximumDetailLength)
            detail = detail[..MaximumDetailLength];
        return new(version, kind, status, detail);
    }

    public static bool IsCompatible(string clientVersion, string minimumClientVersion)
    {
        if (!Version.TryParse(clientVersion, out var client) ||
            !Version.TryParse(minimumClientVersion, out var minimum))
            return false;
        return client.Major == minimum.Major && client >= minimum;
    }

    private static string RequireBounded(string? value, string fieldName, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0 || normalized.Length > maximumLength)
            throw new ArgumentException($"{fieldName} muss zwischen 1 und {maximumLength} Zeichen lang sein.", fieldName);
        return normalized;
    }
}
