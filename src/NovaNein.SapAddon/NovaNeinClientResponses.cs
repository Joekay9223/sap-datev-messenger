using System.Globalization;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace NovaNein.SapAddon;

[DataContract]
public sealed class NovaNeinDocumentEvent
{
    [DataMember(Name = "documentId", IsRequired = true, Order = 1)]
    public Guid DocumentId { get; set; }

    [DataMember(Name = "occurredAt", IsRequired = true, Order = 2)]
    public string OccurredAtText { get; set; } = string.Empty;

    [DataMember(Name = "kind", IsRequired = true, Order = 3)]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "detail", IsRequired = true, Order = 4)]
    public string Detail { get; set; } = string.Empty;

    [DataMember(Name = "actor", IsRequired = true, Order = 5)]
    public string Actor { get; set; } = string.Empty;

    [IgnoreDataMember]
    public DateTimeOffset OccurredAt => ParseTimestamp(OccurredAtText, "Ereigniszeitpunkt");

    private static DateTimeOffset ParseTimestamp(string value, string fieldName) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp
            : throw new InvalidDataException($"Der NovaNein-{fieldName} ist ungültig.");
}

[DataContract]
public sealed class NovaNeinUserNotification
{
    [DataMember(Name = "id", IsRequired = true, Order = 1)]
    public long Id { get; set; }

    [DataMember(Name = "recipient", IsRequired = true, Order = 2)]
    public string Recipient { get; set; } = string.Empty;

    [DataMember(Name = "createdAt", IsRequired = true, Order = 3)]
    public string CreatedAtText { get; set; } = string.Empty;

    [DataMember(Name = "title", IsRequired = true, Order = 4)]
    public string Title { get; set; } = string.Empty;

    [DataMember(Name = "body", IsRequired = true, Order = 5)]
    public string Body { get; set; } = string.Empty;

    [DataMember(Name = "isRead", IsRequired = true, Order = 6)]
    public bool IsRead { get; set; }

    [IgnoreDataMember]
    public DateTimeOffset CreatedAt =>
        DateTimeOffset.TryParse(CreatedAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp
            : throw new InvalidDataException("Der Zeitpunkt des NovaNein-Hinweises ist ungültig.");
}

[DataContract]
public sealed class NovaNeinHealthResponse
{
    [DataMember(Name = "serverVersion", IsRequired = true, Order = 1)]
    public string ServerVersion { get; set; } = string.Empty;

    [DataMember(Name = "compatible", IsRequired = true, Order = 2)]
    public bool Compatible { get; set; }

    [DataMember(Name = "checkedAt", IsRequired = true, Order = 3)]
    public string CheckedAtText { get; set; } = string.Empty;

    [DataMember(Name = "workstation", IsRequired = true, Order = 4)]
    public NovaNeinWorkstationHealth Workstation { get; set; } = new();

    [IgnoreDataMember]
    public DateTimeOffset CheckedAt => ParseRoundtripTimestamp(CheckedAtText, "Health-Zeitpunkt");

    private static DateTimeOffset ParseRoundtripTimestamp(string value, string fieldName) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp
            : throw new InvalidDataException($"Der NovaNein-{fieldName} ist ungültig.");
}

[DataContract]
public sealed class NovaNeinWorkstationHealth
{
    [DataMember(Name = "workstationName", IsRequired = true, Order = 1)]
    public string WorkstationName { get; set; } = string.Empty;

    [DataMember(Name = "clientVersion", IsRequired = true, Order = 2)]
    public string ClientVersion { get; set; } = string.Empty;

    [DataMember(Name = "clientKind", IsRequired = true, Order = 3)]
    public string ClientKind { get; set; } = string.Empty;

    [DataMember(Name = "status", IsRequired = true, Order = 4)]
    public string Status { get; set; } = string.Empty;

    [DataMember(Name = "detail", IsRequired = true, Order = 5)]
    public string Detail { get; set; } = string.Empty;
}

[DataContract]
public sealed class NovaNeinStatisticsResponse
{
    [DataMember(Name = "total", IsRequired = true, Order = 1)] public int Total { get; set; }
    [DataMember(Name = "received", IsRequired = true, Order = 2)] public int Received { get; set; }
    [DataMember(Name = "needsReview", IsRequired = true, Order = 3)] public int NeedsReview { get; set; }
    [DataMember(Name = "approved", IsRequired = true, Order = 4)] public int Approved { get; set; }
    [DataMember(Name = "rejected", IsRequired = true, Order = 5)] public int Rejected { get; set; }
    [DataMember(Name = "failed", IsRequired = true, Order = 6)] public int Failed { get; set; }
    [DataMember(Name = "attachedToSap", IsRequired = true, Order = 7)] public int AttachedToSap { get; set; }
    [DataMember(Name = "createdLast7Days", IsRequired = true, Order = 8)] public int CreatedLast7Days { get; set; }
    [DataMember(Name = "createdLast30Days", IsRequired = true, Order = 9)] public int CreatedLast30Days { get; set; }
}

[DataContract]
public sealed class NovaNeinAttachmentGap
{
    [DataMember(Name = "kind", IsRequired = true, Order = 1)] public string Kind { get; set; } = string.Empty;
    [DataMember(Name = "docEntry", IsRequired = true, Order = 2)] public int DocEntry { get; set; }
    [DataMember(Name = "docNum", IsRequired = true, Order = 3)] public int DocNum { get; set; }
    [DataMember(Name = "entryDate", IsRequired = true, Order = 4)] public string EntryDateText { get; set; } = string.Empty;
    [DataMember(Name = "attachmentEntry", Order = 5)] public int? AttachmentEntry { get; set; }

    [IgnoreDataMember]
    public DateTime EntryDate => DateTime.TryParse(EntryDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
        ? value
        : throw new InvalidDataException("NovaNein lieferte ein ungültiges SAP-Eingabedatum.");

    [IgnoreDataMember]
    public string FriendlyKind => Kind switch
    {
        "PurchaseInvoice" => "Eingangsrechnung",
        "Invoice" => "Ausgangsrechnung",
        "PurchaseCreditNote" => "Eingangsgutschrift",
        "CreditNote" => "Ausgangsgutschrift",
        _ => Kind
    };
}

[DataContract]
public sealed class NovaNeinMissingOutgoingItem
{
    [DataMember(Name = "docEntry", IsRequired = true, Order = 1)] public int DocEntry { get; set; }
    [DataMember(Name = "docNum", IsRequired = true, Order = 2)] public int DocNum { get; set; }
    [DataMember(Name = "pdfState", Order = 3)] public string PdfState { get; set; } = string.Empty;
    [DataMember(Name = "ignored", Order = 4)] public bool Ignored { get; set; }
}

[DataContract]
public sealed class NovaNeinMissingOutgoingPage
{
    [DataMember(Name = "items", Order = 1)] public NovaNeinMissingOutgoingItem[] Items { get; set; } = Array.Empty<NovaNeinMissingOutgoingItem>();
}

[DataContract]
internal sealed class NovaNeinDatevStatusResponse
{
    [DataMember(Name = "pdfArchived", IsRequired = true, Order = 1)] public bool PdfArchived { get; set; }
    [DataMember(Name = "packagePreparedAt", Order = 2)] public string? PackagePreparedAtText { get; set; }
    [DataMember(Name = "packageFileName", Order = 3)] public string? PackageFileName { get; set; }
    [DataMember(Name = "packageSha256", Order = 4)] public string? PackageSha256 { get; set; }
    [DataMember(Name = "uploadSucceededAt", Order = 5)] public string? UploadSucceededAtText { get; set; }
    [DataMember(Name = "jobFinalizedAt", Order = 6)] public string? JobFinalizedAtText { get; set; }
    [DataMember(Name = "transferred", IsRequired = true, Order = 7)] public bool Transferred { get; set; }
}

[DataContract]
internal sealed class NovaNeinReminderResponse
{
    [DataMember(Name = "enabled", IsRequired = true, Order = 1)]
    public bool Enabled { get; set; }
}

[DataContract]
internal sealed class NovaNeinHealthReport
{
    public NovaNeinHealthReport(string clientVersion, string clientKind, string status, string detail)
    {
        ClientVersion = clientVersion;
        ClientKind = clientKind;
        Status = status;
        Detail = detail;
    }

    [DataMember(Name = "clientVersion", IsRequired = true, Order = 1)]
    public string ClientVersion { get; private set; }

    [DataMember(Name = "clientKind", IsRequired = true, Order = 2)]
    public string ClientKind { get; private set; }

    [DataMember(Name = "status", IsRequired = true, Order = 3)]
    public string Status { get; private set; }

    [DataMember(Name = "detail", IsRequired = true, Order = 4)]
    public string Detail { get; private set; }
}

[DataContract]
internal sealed class NovaNeinReviewRequest
{
    public NovaNeinReviewRequest(bool approve, string reason)
    {
        Approve = approve;
        Reason = reason;
    }

    [DataMember(Name = "approve", IsRequired = true, Order = 1)]
    public bool Approve { get; private set; }

    [DataMember(Name = "reason", IsRequired = true, Order = 2)]
    public string Reason { get; private set; }
}

public static class NovaNeinClientJson
{
    public static IReadOnlyList<NovaNeinDocumentEvent> ParseDocumentEvents(string json)
    {
        var events = DeserializeArray<NovaNeinDocumentEvent>(json, "Dokumentereignisse");
        foreach (var item in events)
        {
            if (item.DocumentId == Guid.Empty || string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.Detail))
                throw new InvalidDataException("NovaNein lieferte ein unvollständiges Dokumentereignis.");
            _ = item.OccurredAt;
        }
        return events;
    }

    public static IReadOnlyList<NovaNeinUserNotification> ParseNotifications(string json)
    {
        var notifications = DeserializeArray<NovaNeinUserNotification>(json, "Hinweise");
        foreach (var item in notifications)
        {
            if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Body))
                throw new InvalidDataException("NovaNein lieferte einen unvollständigen Hinweis.");
            _ = item.CreatedAt;
        }
        return notifications;
    }

    public static IReadOnlyList<string> ValidationReasons(Guid documentId, IEnumerable<NovaNeinDocumentEvent> events)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("Eine Dokument-ID ist erforderlich.", nameof(documentId));
        if (events is null) throw new ArgumentNullException(nameof(events));
        var reasons = events
            .Where(item => item.DocumentId == documentId && string.Equals(item.Kind, "ValidationCompleted", StringComparison.Ordinal))
            .Select(item => item.Detail.Trim())
            .Where(detail => detail.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (reasons.Length == 0) throw new InvalidDataException("NovaNein lieferte für den Prüfbeleg keinen nachvollziehbaren Prüfgrund.");
        return reasons;
    }

    public static string SerializeReviewRequest(bool approve, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Für die manuelle Freigabe oder Ablehnung ist eine Begründung erforderlich.", nameof(reason));
        return Serialize(new NovaNeinReviewRequest(approve, reason.Trim()));
    }

    public static string SerializeHealthReport(string clientVersion, string clientKind, string status, string detail)
    {
        if (string.IsNullOrWhiteSpace(clientVersion)) throw new ArgumentException("Die Clientversion ist erforderlich.", nameof(clientVersion));
        if (string.IsNullOrWhiteSpace(clientKind)) throw new ArgumentException("Der Clienttyp ist erforderlich.", nameof(clientKind));
        if (status is not ("ok" or "degraded" or "error")) throw new ArgumentException("Der Health-Status ist ungültig.", nameof(status));
        return Serialize(new NovaNeinHealthReport(clientVersion.Trim(), clientKind.Trim(), status, (detail ?? string.Empty).Trim()));
    }

    public static NovaNeinHealthResponse ParseHealthResponse(string json)
    {
        var health = DeserializeObject<NovaNeinHealthResponse>(json, "Health-Antwort");
        if (string.IsNullOrWhiteSpace(health.ServerVersion) || health.Workstation is null ||
            string.IsNullOrWhiteSpace(health.Workstation.WorkstationName) ||
            string.IsNullOrWhiteSpace(health.Workstation.ClientVersion) ||
            health.Workstation.Status is not ("ok" or "degraded" or "error"))
            throw new InvalidDataException("NovaNein lieferte eine unvollständige Health-Antwort.");
        _ = health.CheckedAt;
        return health;
    }

    public static NovaNeinStatisticsResponse ParseStatisticsResponse(string json)
    {
        var statistics = DeserializeObject<NovaNeinStatisticsResponse>(json, "Statistik-Antwort");
        if (new[] { statistics.Total, statistics.Received, statistics.NeedsReview, statistics.Approved,
                statistics.Rejected, statistics.Failed, statistics.AttachedToSap,
                statistics.CreatedLast7Days, statistics.CreatedLast30Days }.Any(value => value < 0))
            throw new InvalidDataException("NovaNein lieferte negative Statistikwerte.");
        return statistics;
    }

    public static IReadOnlyList<NovaNeinAttachmentGap> ParseAttachmentGaps(string json)
    {
        var gaps = DeserializeArray<NovaNeinAttachmentGap>(json, "PDF-Scan-Ergebnisse");
        foreach (var item in gaps)
        {
            if (string.IsNullOrWhiteSpace(item.Kind) || item.DocEntry <= 0 || item.DocNum <= 0)
                throw new InvalidDataException("NovaNein lieferte einen unvollständigen PDF-Scan-Treffer.");
            _ = item.EntryDate;
        }
        return gaps;
    }

    public static IReadOnlyList<NovaNeinMissingOutgoingItem> ParseMissingOutgoingItems(string json)
    {
        var page = DeserializeObject<NovaNeinMissingOutgoingPage>(json, "offene Ausgangsbelege");
        return page.Items
            .Where(item => item.DocEntry > 0 && item.DocNum > 0 && !item.Ignored && string.Equals(item.PdfState, "missing", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static bool ParseReminderEnabled(string json)
    {
        var response = DeserializeObject<NovaNeinReminderResponse>(json, "Reminder-Einstellung");
        return response.Enabled;
    }

    public static NovaNeinDatevStatus ParseDatevStatus(string json)
    {
        var response = DeserializeObject<NovaNeinDatevStatusResponse>(json, "DATEV-Status");
        return new(response.PdfArchived, ParseOptional(response.PackagePreparedAtText), response.PackageFileName, response.PackageSha256,
            ParseOptional(response.UploadSucceededAtText), ParseOptional(response.JobFinalizedAtText), response.Transferred);
    }

    private static DateTimeOffset? ParseOptional(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : throw new InvalidDataException("NovaNein lieferte einen ungültigen DATEV-Zeitpunkt.");

    private static T[] DeserializeArray<T>(string json, string responseName)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException($"NovaNein lieferte keine {responseName}.");
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return new DataContractJsonSerializer(typeof(T[])).ReadObject(stream) as T[]
                ?? throw new InvalidDataException($"NovaNein lieferte ungültige {responseName}.");
        }
        catch (SerializationException ex)
        {
            throw new InvalidDataException($"NovaNein lieferte ungültige {responseName}.", ex);
        }
    }

    private static T DeserializeObject<T>(string json, string responseName)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException($"NovaNein lieferte keine {responseName}.");
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(stream)
                ?? throw new InvalidDataException($"NovaNein lieferte eine ungültige {responseName}."));
        }
        catch (SerializationException ex)
        {
            throw new InvalidDataException($"NovaNein lieferte eine ungültige {responseName}.", ex);
        }
    }

    private static string Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public static class NovaNeinHealthRetryPolicy
{
    public const int HealthyIntervalMilliseconds = 60_000;

    public static int NextDelayMilliseconds(int consecutiveFailures)
    {
        if (consecutiveFailures < 0) throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));
        if (consecutiveFailures == 0) return HealthyIntervalMilliseconds;
        var exponent = Math.Min(consecutiveFailures - 1, 4);
        return Math.Min(60_000, 5_000 * (1 << exponent));
    }
}

public static class NovaNeinNotificationPresentation
{
    public static string Format(IReadOnlyList<NovaNeinUserNotification> notifications, int maximum = 10)
    {
        if (notifications is null) throw new ArgumentNullException(nameof(notifications));
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        var ordered = notifications.OrderByDescending(item => item.CreatedAt).ToArray();
        var builder = new StringBuilder("Diese Hinweise werden im NovaNein-SAP-Fenster angezeigt. Es wurde keine E-Mail versandt.");
        foreach (var item in ordered.Take(maximum))
        {
            builder.AppendLine().AppendLine();
            builder.Append(item.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture));
            builder.Append(" – ").Append(item.Title.Trim()).AppendLine();
            builder.Append(ToFriendlyBody(item.Body));
        }
        if (ordered.Length > maximum)
            builder.AppendLine().AppendLine().Append(ordered.Length - maximum).Append(" weitere Hinweise sind auf dem Server vorhanden.");
        return builder.ToString();
    }

    public static string ToFriendlyBody(string body) => (body ?? string.Empty).Trim()
        .Replace("PurchaseCreditNote", "Eingangsgutschrift")
        .Replace("PurchaseInvoice", "Eingangsrechnung")
        .Replace("CreditNote", "Ausgangsgutschrift")
        .Replace("Invoice", "Ausgangsrechnung")
        .Replace("DocEntry", "SAP-Schlüssel");
}
