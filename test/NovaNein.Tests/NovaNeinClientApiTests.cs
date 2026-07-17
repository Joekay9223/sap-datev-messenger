using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using NovaNein.SapAddon;

namespace NovaNein.Tests;

public sealed class NovaNeinClientApiTests
{
    [Fact]
    public void Parses_validation_reasons_without_losing_escaped_text()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            [{"documentId":"{{id}}","occurredAt":"2026-07-11T08:30:00+02:00","kind":"DocumentReceived","detail":"PDF übernommen.","actor":"server"},
             {"documentId":"{{id}}","occurredAt":"2026-07-11T08:31:00+02:00","kind":"ValidationCompleted","detail":"Betrag weicht ab: \"119,00 EUR\".\nBitte prüfen.","actor":"OpenAI"}]
            """;

        var events = NovaNeinClientJson.ParseDocumentEvents(json);
        var reasons = NovaNeinClientJson.ValidationReasons(id, events);

        Assert.Single(reasons);
        Assert.Contains("\"119,00 EUR\"", reasons[0], StringComparison.Ordinal);
        Assert.Contains("Bitte prüfen.", reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_yellow_review_without_a_traceable_validation_reason()
    {
        var id = Guid.NewGuid();
        var events = NovaNeinClientJson.ParseDocumentEvents($$"""[{"documentId":"{{id}}","occurredAt":"2026-07-11T08:30:00Z","kind":"DocumentReceived","detail":"PDF übernommen.","actor":"server"}]""");

        Assert.Throws<InvalidDataException>(() => NovaNeinClientJson.ValidationReasons(id, events));
    }

    [Fact]
    public void Parses_and_formats_monday_notes_as_in_app_messages_not_email()
    {
        var json = """
            [{"id":17,"recipient":"WORKSTATION","createdAt":"2026-07-06T08:00:00+02:00","title":"NovaNein Wochen-Reminder","body":"05.07.2026: PurchaseInvoice 900402 (DocEntry 80404) ohne PDF-Anhang.","isRead":false}]
            """;

        var notifications = NovaNeinClientJson.ParseNotifications(json);
        var text = NovaNeinNotificationPresentation.Format(notifications);

        Assert.Single(notifications);
        Assert.Contains("Wochen-Reminder", text, StringComparison.Ordinal);
        Assert.Contains("Eingangsrechnung 900402", text, StringComparison.Ordinal);
        Assert.Contains("SAP-Schlüssel 80404", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PurchaseInvoice", text, StringComparison.Ordinal);
        Assert.Contains("keine E-Mail versandt", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_payload_requires_and_preserves_an_explicit_reason()
    {
        Assert.Throws<ArgumentException>(() => NovaNeinClientJson.SerializeReviewRequest(approve: true, "  "));

        var json = NovaNeinClientJson.SerializeReviewRequest(approve: false, "  Betrag fachlich falsch  ");
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("approve").GetBoolean());
        Assert.Equal("Betrag fachlich falsch", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Parses_scan_results_into_user_facing_rows_instead_of_raw_json()
    {
        var gaps = NovaNeinClientJson.ParseAttachmentGaps("""
            [{"kind":"PurchaseInvoice","docEntry":80404,"docNum":900402,"entryDate":"2026-07-10","attachmentEntry":null},
             {"kind":"CreditNote","docEntry":80405,"docNum":900403,"entryDate":"2026-07-11","attachmentEntry":17}]
            """);

        Assert.Equal(2, gaps.Count);
        Assert.Equal("Eingangsrechnung", gaps[0].FriendlyKind);
        Assert.Equal(new DateTime(2026, 7, 10), gaps[0].EntryDate);
        Assert.Null(gaps[0].AttachmentEntry);
        Assert.Equal("Ausgangsgutschrift", gaps[1].FriendlyKind);
    }

    [Fact]
    public void Parses_separate_archive_datev_and_transfer_timestamps()
    {
        var status = NovaNeinClientJson.ParseDatevStatus("""
            {"pdfArchived":true,"packagePreparedAt":"2026-07-12T12:09:59Z","packageFileName":"Ausgangsrechnung-910381.zip","packageSha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","uploadSucceededAt":null,"jobFinalizedAt":null,"transferred":false}
            """);
        Assert.True(status.PdfArchived);
        Assert.Equal("Ausgangsrechnung-910381.zip", status.PackageFileName);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 12, 9, 59, TimeSpan.Zero), status.PackagePreparedAt);
        Assert.Null(status.UploadSucceededAt);
        Assert.False(status.Transferred);
    }

    [Fact]
    public async Task Calls_events_reviews_and_notifications_with_the_expected_contract()
    {
        var id = Guid.NewGuid();
        var handler = new ClientApiHandler(id);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://novanein.test/") };
        using var client = new NovaNeinServerClient(http);

        var reasons = await client.GetValidationReasonsAsync(id);
        var reviewed = await client.ReviewDocumentAsync(id, approve: true, "Betrag anhand Bestellung geprüft", "manager");
        var notifications = await client.GetNotificationsAsync();
        var existing = await client.GetDocumentForSapAsync(new SapDocumentContext(SapDocumentDirection.Incoming, 4711, 99, "manager"));
        var archived = await client.DownloadArchivedPdfAsync(new SapDocumentContext(SapDocumentDirection.Incoming, 4711, 99, "manager"));
        var datev = await client.GetDatevStatusAsync(id);

        Assert.Single(reasons);
        Assert.Equal(NovaNeinDocumentStatus.Approved, reviewed.Status);
        Assert.Single(notifications);
        Assert.Equal(NovaNeinDocumentStatus.NeedsReview, existing!.Status);
        Assert.Equal("rechnung.pdf", archived!.FileName);
        Assert.True(datev.PdfArchived);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(archived.Content, 0, 5));
        Assert.Equal(6, handler.Requests.Count);
        var review = Assert.Single(handler.Requests.Where(item => item.Method == HttpMethod.Post));
        Assert.Equal($"/api/v1/documents/{id:D}/reviews", review.Path);
        Assert.Equal("manager", review.SapUser);
        using var body = JsonDocument.Parse(review.Body!);
        Assert.True(body.RootElement.GetProperty("approve").GetBoolean());
        Assert.Equal("Betrag anhand Bestellung geprüft", body.RootElement.GetProperty("reason").GetString());
    }

    private sealed class ClientApiHandler(Guid documentId) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? SapUser, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var sapUser = request.Headers.TryGetValues("X-NovaNein-Sap-User", out var values) ? values.Single() : null;
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, sapUser, body));
            if (request.RequestUri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
                return Json($$"""[{"documentId":"{{documentId}}","occurredAt":"2026-07-11T08:31:00Z","kind":"ValidationCompleted","detail":"Steuersatz muss geprüft werden.","actor":"OpenAI"}]""");
            if (request.RequestUri.AbsolutePath.EndsWith("/reviews", StringComparison.Ordinal))
                return Json($$"""{"id":"{{documentId}}","status":4,"signal":1}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/notifications", StringComparison.Ordinal))
                return Json("""[{"id":17,"recipient":"WORKSTATION","createdAt":"2026-07-06T08:00:00Z","title":"NovaNein Wochen-Reminder","body":"Keine PDF vorhanden.","isRead":false}]""");
            if (request.RequestUri.AbsolutePath.EndsWith("/by-sap/incoming/4711", StringComparison.Ordinal))
                return Json($$"""{"id":"{{documentId}}","status":2,"signal":1}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/by-sap/incoming/4711/pdf", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-test")) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "rechnung.pdf" };
                return response;
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/datev", StringComparison.Ordinal))
                return Json("{\"pdfArchived\":true,\"packagePreparedAt\":\"2026-07-12T12:09:59Z\",\"packageFileName\":\"Ausgangsrechnung-910381.zip\",\"packageSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"uploadSucceededAt\":null,\"jobFinalizedAt\":null,\"transferred\":false}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
