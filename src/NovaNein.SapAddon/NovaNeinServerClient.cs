using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace NovaNein.SapAddon;

public sealed class NovaNeinServerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _disposeHttpClient;

    public NovaNeinServerClient(Uri serverBaseUri, string certificateThumbprint)
    {
        if (serverBaseUri is null || serverBaseUri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Der NovaNein-Server muss per HTTPS angesprochen werden.", nameof(serverBaseUri));
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certificate = store.Certificates.Find(X509FindType.FindByThumbprint, certificateThumbprint, validOnly: false)
            .OfType<X509Certificate2>().SingleOrDefault();
        if (certificate is null || !certificate.HasPrivateKey) throw new InvalidOperationException("Das NovaNein-Arbeitsplatzzertifikat wurde nicht im LocalMachine-Zertifikatsspeicher gefunden.");
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(certificate);
        _http = new HttpClient(handler) { BaseAddress = serverBaseUri, Timeout = TimeSpan.FromSeconds(30) };
        _disposeHttpClient = true;
    }

    internal NovaNeinServerClient(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_http.BaseAddress is null || _http.BaseAddress.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Der NovaNein-Testclient benötigt eine HTTPS-Basisadresse.", nameof(httpClient));
    }

    public Task<NovaNeinDocumentProgress> SubmitOutgoingPdfAsync(int docEntry, int docNum, string pdfPath, string sapUser, CancellationToken cancellationToken = default) =>
        SubmitPdfAsync(new SapDocumentContext(SapDocumentDirection.Outgoing, docEntry, docNum, sapUser), pdfPath, cancellationToken);

    public Task<NovaNeinDocumentProgress> SubmitIncomingPdfAsync(int docEntry, int docNum, string pdfPath, string sapUser, CancellationToken cancellationToken = default) =>
        SubmitPdfAsync(new SapDocumentContext(SapDocumentDirection.Incoming, docEntry, docNum, sapUser), pdfPath, cancellationToken);

    public async Task<IReadOnlyList<NovaNeinMissingOutgoingItem>> GetMissingOutgoingItemsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/work-items?direction=outgoing&pdfPresent=false&page=1&pageSize=500", cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Die offenen Ausgangsbelege konnten nicht gelesen werden ({(int)response.StatusCode}): {payload}");
        return NovaNeinClientJson.ParseMissingOutgoingItems(payload);
    }

    public async Task<NovaNeinDocumentProgress> SubmitPdfAsync(SapDocumentContext context, string pdfPath, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("Die PDF-Datei wurde nicht gefunden.", pdfPath);
        using var file = File.OpenRead(pdfPath);
        var signature = new byte[5];
        if (await file.ReadAsync(signature, 0, signature.Length, cancellationToken) != signature.Length || System.Text.Encoding.ASCII.GetString(signature) != "%PDF-") throw new InvalidDataException("Die Datei besitzt keine gültige PDF-Signatur.");
        file.Position = 0;
        using var body = new MultipartFormDataContent();
        body.Add(new StringContent(context.DocNum.ToString(System.Globalization.CultureInfo.InvariantCulture)), "docNum");
        if (context.Direction == SapDocumentDirection.Incoming) body.Add(new StringContent(context.DocEntry.ToString(System.Globalization.CultureInfo.InvariantCulture)), "docEntry");
        var pdf = new StreamContent(file); pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        body.Add(pdf, "pdf", Path.GetFileName(pdfPath));
        var path = context.Direction == SapDocumentDirection.Incoming ? "api/v1/documents/incoming" : $"api/v1/documents/outgoing/{context.DocEntry}/generate";
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        AddSapUserHeader(request, context.SapUser);
        using var response = await _http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"NovaNein-Server hat den Beleg abgelehnt ({(int)response.StatusCode}): {payload}");
        return await WaitForProgressAsync(ParseProgress(payload), cancellationToken);
    }

    private async Task<NovaNeinDocumentProgress> WaitForProgressAsync(NovaNeinDocumentProgress current, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60 && !current.IsTerminal; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            using var response = await _http.GetAsync($"api/v1/documents/{current.Id}", cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Der NovaNein-Prüfstatus konnte nicht gelesen werden ({(int)response.StatusCode}).");
            current = ParseProgress(await response.Content.ReadAsStringAsync());
        }
        return current;
    }

    public async Task<NovaNeinDocumentProgress?> GetDocumentForSapAsync(SapDocumentContext context, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var direction = context.Direction == SapDocumentDirection.Incoming ? "incoming" : "outgoing";
        using var response = await _http.GetAsync($"api/v1/documents/by-sap/{direction}/{context.DocEntry}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Der vorhandene NovaNein-Belegstatus konnte nicht gelesen werden ({(int)response.StatusCode}): {payload}");
        return ParseProgress(payload);
    }

    public async Task<NovaNeinDatevStatus> GetDatevStatusAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("Eine Dokument-ID ist erforderlich.", nameof(documentId));
        using var response = await _http.GetAsync($"api/v1/documents/{documentId:D}/datev", cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Der DATEV-Status konnte nicht gelesen werden ({(int)response.StatusCode}): {payload}");
        return NovaNeinClientJson.ParseDatevStatus(payload);
    }

    public async Task<NovaNeinArchivedPdf?> DownloadArchivedPdfAsync(SapDocumentContext context, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var direction = context.Direction == SapDocumentDirection.Incoming ? "incoming" : "outgoing";
        using var response = await _http.GetAsync($"api/v1/documents/by-sap/{direction}/{context.DocEntry}/pdf", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var payload = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Die archivierte NovaNein-PDF konnte nicht gelesen werden ({(int)response.StatusCode}).");
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        fileName = Path.GetFileName((fileName ?? string.Empty).Trim('"'));
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"NovaNein-{direction}-{context.DocNum}.pdf";
        return new NovaNeinArchivedPdf(fileName, payload);
    }

    public async Task<IReadOnlyList<NovaNeinDocumentEvent>> GetDocumentEventsAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("Eine Dokument-ID ist erforderlich.", nameof(documentId));
        using var response = await _http.GetAsync($"api/v1/documents/{documentId:D}/events", cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Die NovaNein-Prüfgründe konnten nicht gelesen werden ({(int)response.StatusCode}): {payload}");
        return NovaNeinClientJson.ParseDocumentEvents(payload);
    }

    public async Task<IReadOnlyList<string>> GetValidationReasonsAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        NovaNeinClientJson.ValidationReasons(documentId, await GetDocumentEventsAsync(documentId, cancellationToken));

    public async Task<NovaNeinDocumentProgress> ReviewDocumentAsync(Guid documentId, bool approve, string reason, string sapUser, CancellationToken cancellationToken = default)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("Eine Dokument-ID ist erforderlich.", nameof(documentId));
        var json = NovaNeinClientJson.SerializeReviewRequest(approve, reason);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/documents/{documentId:D}/reviews")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        AddSapUserHeader(request, sapUser);
        using var response = await _http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Die manuelle NovaNein-Entscheidung wurde abgelehnt ({(int)response.StatusCode}): {payload}");
        var progress = ParseProgress(payload);
        var expectedStatus = approve ? NovaNeinDocumentStatus.Approved : NovaNeinDocumentStatus.Rejected;
        if (progress.Id != documentId || progress.Status != expectedStatus)
            throw new InvalidDataException("NovaNein hat die manuelle Entscheidung nicht mit dem erwarteten Status bestätigt.");
        return progress;
    }

    public async Task<IReadOnlyList<NovaNeinUserNotification>> GetNotificationsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/notifications", cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NovaNein-Hinweise konnten nicht gelesen werden ({(int)response.StatusCode}): {payload}");
        return NovaNeinClientJson.ParseNotifications(payload);
    }

    public async Task MarkNotificationReadAsync(long notificationId, CancellationToken cancellationToken = default)
    {
        if (notificationId <= 0) throw new ArgumentOutOfRangeException(nameof(notificationId));
        using var response = await _http.PostAsync($"api/v1/notifications/{notificationId}/read", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var payload = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"NovaNein-Hinweis konnte nicht quittiert werden ({(int)response.StatusCode}): {payload}");
        }
    }

    public async Task<NovaNeinHealthResponse> CheckHealthAsync(
        string clientVersion,
        string detail,
        CancellationToken cancellationToken = default)
    {
        var json = NovaNeinClientJson.SerializeHealthReport(clientVersion, "sap-addon", "ok", detail);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/client-health")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NovaNein-Selbstprüfung fehlgeschlagen ({(int)response.StatusCode}): {payload}");
        return NovaNeinClientJson.ParseHealthResponse(payload);
    }

    private static NovaNeinDocumentProgress ParseProgress(string payload)
    {
        payload ??= string.Empty;
        var id = Regex.Match(payload, "\\\"id\\\"\\s*:\\s*\\\"(?<value>[0-9a-fA-F-]{36})\\\"");
        var status = Regex.Match(payload, "\\\"status\\\"\\s*:\\s*(?<value>\\d+)");
        if (!id.Success || !Guid.TryParse(id.Groups["value"].Value, out var documentId) ||
            !status.Success || !int.TryParse(status.Groups["value"].Value, out var documentStatus) ||
            !Enum.IsDefined(typeof(NovaNeinDocumentStatus), documentStatus))
            throw new InvalidDataException("Der NovaNein-Server lieferte keinen gültigen Belegstatus.");
        var signal = Regex.Match(payload, "\\\"signal\\\"\\s*:\\s*(?<value>\\d+)");
        return new(documentId, (NovaNeinDocumentStatus)documentStatus, signal.Success && int.TryParse(signal.Groups["value"].Value, out var value) ? value : (int?)null);
    }

    private static void AddSapUserHeader(HttpRequestMessage request, string sapUser) =>
        request.Headers.Add("X-NovaNein-Sap-User", string.IsNullOrWhiteSpace(sapUser) ? "unbekannt" : sapUser.Trim());

    public async Task<IReadOnlyList<NovaNeinAttachmentGap>> ScanMissingPdfAttachmentsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/scans/missing-pdf", cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"PDF-Anhangscan konnte nicht ausgeführt werden ({(int)response.StatusCode}): {payload}");
        return NovaNeinClientJson.ParseAttachmentGaps(payload);
    }

    public async Task<NovaNeinStatisticsResponse> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/statistics/summary", cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NovaNein-Statistik konnte nicht gelesen werden ({(int)response.StatusCode}): {payload}");
        return NovaNeinClientJson.ParseStatisticsResponse(payload);
    }

    public Task EnableWeeklyReminderAsync(CancellationToken cancellationToken = default) =>
        SetWeeklyReminderAsync(true, cancellationToken);

    public async Task<bool> GetWeeklyReminderAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/reminders/weekly", cancellationToken);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Reminder-Einstellung konnte nicht gelesen werden ({(int)response.StatusCode}): {payload}");
        return NovaNeinClientJson.ParseReminderEnabled(payload);
    }

    public async Task SetWeeklyReminderAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var json = $"{{\"enabled\":{(enabled ? "true" : "false")}}}";
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/v1/reminders/weekly")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Wochen-Reminder konnte nicht geändert werden ({(int)response.StatusCode}).");
    }

    public void Dispose()
    {
        if (_disposeHttpClient) _http.Dispose();
    }
}
