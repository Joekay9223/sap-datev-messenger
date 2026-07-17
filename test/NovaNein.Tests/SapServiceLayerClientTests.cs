using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class SapServiceLayerClientTests
{
    [Fact]
    public async Task Reads_purchase_invoice_after_service_layer_login()
    {
        var handler = new RecordingHandler();
        var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, Configuration("read-only"));
        var document = await client.GetDocumentAsync(SapDocumentKind.PurchaseInvoice, 4711);
        Assert.Equal(4711, document.DocEntry);
        Assert.Equal(99, document.DocNum);
        Assert.Equal("RE-42", document.InvoiceNumber);
        Assert.Equal("Bitte Ursprungsbeleg beachten.", document.Comments);
        Assert.Equal(12, document.AttachmentEntry);
        Assert.Equal(3456, document.TransId);
        Assert.Contains(handler.Requests, x => x.Uri!.Contains("Comments", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, x => x.Uri!.Contains("TransNum", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, x => x.Uri!.Contains("TransId", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, x => x.Uri!.Contains("PurchaseInvoices(4711)") && x.Cookie == "B1SESSION=session-1");
    }

    [Fact]
    public async Task Sends_service_layer_login_fields_with_sap_exact_casing()
    {
        var handler = new LoginBodyHandler();
        var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, Configuration("read-only"));

        await client.GetDocumentAsync(SapDocumentKind.PurchaseInvoice, 4711);

        Assert.Contains("\"CompanyDB\":\"TEST\"", handler.LoginBody, StringComparison.Ordinal);
        Assert.Contains("\"UserName\":\"reader\"", handler.LoginBody, StringComparison.Ordinal);
        Assert.Contains("\"Password\":\"secret\"", handler.LoginBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"companyDB\"", handler.LoginBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"userName\"", handler.LoginBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\"", handler.LoginBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SapDocumentKind.Invoice, "Invoices")]
    [InlineData(SapDocumentKind.CreditNote, "CreditNotes")]
    public async Task Reads_outgoing_invoice_number_from_doc_num_instead_of_customer_reference(SapDocumentKind kind, string entity)
    {
        var handler = new RecordingHandler();
        var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, Configuration("read-only"));

        var document = await client.GetDocumentAsync(kind, 4711);

        Assert.Equal("99", document.InvoiceNumber);
        Assert.NotEqual("RE-42", document.InvoiceNumber);
        Assert.Contains(handler.Requests, x => x.Uri!.Contains($"{entity}(4711)") && x.Cookie == "B1SESSION=session-1");
    }

    [Fact]
    public async Task Blocks_attachments2_even_when_write_flag_is_not_enabled()
    {
        var client = new SapServiceLayerClient(new HttpClient(new RecordingHandler()) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, Configuration("read-only"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AttachPdfAsync(SapDocumentKind.Invoice, 1, 1, "C:\\example.pdf"));
    }

    [Fact]
    public async Task Keeps_purchase_invoice_and_supplier_writes_behind_independent_kill_switches()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sap:CompanyDatabase"] = "TEST", ["Sap:UserName"] = "writer", ["Sap:Password"] = "secret",
            ["Sap:Mode"] = "write-enabled", ["Sap:EnableAttachments2Writes"] = "true",
            ["Sap:EnablePurchaseInvoiceWrites"] = "false", ["Sap:EnableBusinessPartnerWrites"] = "false"
        }).Build();
        var client = new SapServiceLayerClient(new HttpClient(new RecordingHandler()) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, configuration);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreatePurchaseInvoiceAsync(
            new SapPurchaseInvoiceRequest(Guid.NewGuid(), new string('A', 64), "V100", "RE-1",
                new DateOnly(2026, 7, 16), null, null, "EUR", 119m, "C:\\invoice.pdf",
                [new SapPurchaseInvoiceLineRequest(1, "Service", 100m, "4400", "V2")]),
            "reviewer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateSupplierAsync(
            new SapSupplierCreateRequest("V999", "Neu GmbH", "DE123", null, null, null, null, null, "DE", Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task Creates_links_and_reads_back_a_pdf_when_all_write_gates_are_enabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaNeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pdf = Path.Combine(root, "freigegebener-test.pdf");
        await File.WriteAllTextAsync(pdf, "%PDF-test");
        try
        {
            var handler = new AttachmentWriteHandler();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sap:CompanyDatabase"] = "TEST", ["Sap:UserName"] = "writer", ["Sap:Password"] = "secret",
                ["Sap:Mode"] = "write-enabled", ["Sap:EnableAttachments2Writes"] = "true", ["Sap:AttachmentSourceRoot"] = root
            }).Build();
            var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, configuration);

            await client.AttachPdfAsync(SapDocumentKind.PurchaseInvoice, 4711, 99, pdf);

            Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post
                && request.Path == "/b1s/v2/Attachments2"
                && request.Body!.Contains("\"Attachments2_Lines\"", StringComparison.Ordinal)
                && request.Body.Contains("\"FileName\"", StringComparison.Ordinal)
                && request.Body.Contains("freigegebener-test", StringComparison.Ordinal));
            Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Patch
                && request.Path == "/b1s/v2/PurchaseInvoices(4711)"
                && request.Body!.Contains("\"AttachmentEntry\"", StringComparison.Ordinal)
                && request.Body.Contains("136", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Requests, request => request.Body?.Contains("\"attachments2_Lines\"", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(handler.Requests, request => request.Body?.Contains("\"attachmentEntry\"", StringComparison.Ordinal) == true);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Treats_the_same_hash_named_existing_sap_attachment_as_an_idempotent_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaNeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pdf = Path.Combine(root, "freigegebener-test.pdf");
        await File.WriteAllTextAsync(pdf, "%PDF-test");
        try
        {
            var handler = new AttachmentWriteHandler(initiallyLinked: true);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sap:CompanyDatabase"] = "TEST", ["Sap:UserName"] = "writer", ["Sap:Password"] = "secret",
                ["Sap:Mode"] = "write-enabled", ["Sap:EnableAttachments2Writes"] = "true", ["Sap:AttachmentSourceRoot"] = root
            }).Build();
            var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, configuration);

            await client.AttachPdfAsync(SapDocumentKind.PurchaseInvoice, 4711, 99, pdf);

            Assert.Contains(handler.Requests, request => request.Path == "/b1s/v2/Attachments2(136)");
            Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/b1s/v2/Attachments2");
            Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Patch);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Refuses_to_attach_when_service_layer_doc_num_differs_from_the_stored_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaNeinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pdf = Path.Combine(root, "hash.pdf");
        await File.WriteAllTextAsync(pdf, "%PDF-test");
        try
        {
            var handler = new AttachmentWriteHandler();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sap:CompanyDatabase"] = "TEST", ["Sap:UserName"] = "writer", ["Sap:Password"] = "secret",
                ["Sap:Mode"] = "write-enabled", ["Sap:EnableAttachments2Writes"] = "true", ["Sap:AttachmentSourceRoot"] = root
            }).Build();
            var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, configuration);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.AttachPdfAsync(SapDocumentKind.PurchaseInvoice, 4711, 100, pdf));

            Assert.DoesNotContain(handler.Requests, request => request.Method is not null && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch) && !request.Path.EndsWith("/Login"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Refuses_a_pdf_outside_the_explicit_attachment_source_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaNeinTests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(outside, "%PDF-test");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sap:CompanyDatabase"] = "TEST", ["Sap:UserName"] = "writer", ["Sap:Password"] = "secret",
                ["Sap:Mode"] = "write-enabled", ["Sap:EnableAttachments2Writes"] = "true", ["Sap:AttachmentSourceRoot"] = root
            }).Build();
            var client = new SapServiceLayerClient(new HttpClient(new AttachmentWriteHandler()) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, configuration);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.AttachPdfAsync(SapDocumentKind.PurchaseInvoice, 4711, 99, outside));
        }
        finally { Directory.Delete(root, recursive: true); File.Delete(outside); }
    }

    [Fact]
    public async Task Scans_by_creation_date_instead_of_document_date()
    {
        var handler = new RecordingHandler();
        var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, Configuration("read-only"));

        var gaps = await client.FindMissingPdfAttachmentsAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 10));

        Assert.Equal(4, gaps.Count);
        Assert.All(gaps, gap => Assert.Equal(new DateOnly(2026, 7, 2), gap.EntryDate));
        Assert.Equal(4, handler.Requests.Count(x => x.Uri!.Contains("CreationDate", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, x => x.Uri!.Contains("CreateDate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scan_follows_every_service_layer_page()
    {
        var handler = new PagingHandler();
        var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, Configuration("read-only"));

        var gaps = await client.FindMissingPdfAttachmentsAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 10));

        Assert.Contains(gaps, x => x.Kind == SapDocumentKind.Invoice && x.DocNum == 1001);
        Assert.Contains(gaps, x => x.Kind == SapDocumentKind.Invoice && x.DocNum == 1002);
        Assert.Contains(handler.Requests, x => x.Contains("$skiptoken=page-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reauthenticates_once_when_the_service_layer_session_expires()
    {
        var handler = new ExpiringSessionHandler();
        var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, Configuration("read-only"));

        var document = await client.GetDocumentAsync(SapDocumentKind.Invoice, 4711);

        Assert.Equal(4711, document.DocEntry);
        Assert.Equal(2, handler.LoginRequests);
    }

    [Fact]
    public async Task Validates_german_input_tax_codes_against_vat_groups()
    {
        var handler = new RecordingHandler();
        var client = new SapServiceLayerClient(new HttpClient(handler) { BaseAddress = new Uri("https://sap.test/b1s/v2/") }, Configuration("read-only"));

        await client.ValidateTaxCodeAsync("V2");

        Assert.Contains(handler.Requests, request => request.Uri!.Contains("VatGroups('V2')", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.Uri!.Contains("SalesTaxCodes", StringComparison.Ordinal));
    }

    private static IConfiguration Configuration(string mode) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Sap:CompanyDatabase"] = "TEST", ["Sap:UserName"] = "reader", ["Sap:Password"] = "secret", ["Sap:Mode"] = mode
    }).Build();

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string? Uri, string? Cookie)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri?.ToString(), request.Headers.TryGetValues("Cookie", out var values) ? values.Single() : null));
            if (request.RequestUri!.AbsolutePath.EndsWith("/Login")) return Task.FromResult(Json(HttpStatusCode.OK, "{\"SessionId\":\"session-1\"}"));
            if (request.RequestUri.Query.Contains("CreationDate", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"value\":[{\"DocEntry\":4711,\"DocNum\":99,\"CreationDate\":\"2026-07-02T00:00:00Z\",\"AttachmentEntry\":null}]}"));
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"DocEntry\":4711,\"DocNum\":99,\"CardCode\":\"V100\",\"CardName\":\"Example Supplier GmbH\",\"NumAtCard\":\"RE-42\",\"DocDate\":\"2026-07-10\",\"DocTotal\":119.00,\"DocCurrency\":\"EUR\",\"AttachmentEntry\":12,\"TransNum\":3456,\"Comments\":\"Bitte Ursprungsbeleg beachten.\"}"));
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class LoginBodyHandler : HttpMessageHandler
    {
        public string LoginBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Login"))
            {
                LoginBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(HttpStatusCode.OK, "{\"SessionId\":\"session-1\"}");
            }
            return Json(HttpStatusCode.OK, "{\"DocEntry\":4711,\"DocNum\":99,\"CardCode\":\"V100\",\"CardName\":\"Example Supplier GmbH\",\"NumAtCard\":\"RE-42\",\"DocDate\":\"2026-07-10\",\"DocTotal\":119.00,\"DocCurrency\":\"EUR\",\"AttachmentEntry\":12}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class PagingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);
            if (request.RequestUri.AbsolutePath.EndsWith("/Login")) return Task.FromResult(Json(HttpStatusCode.OK, "{\"SessionId\":\"session-1\"}"));
            if (uri.Contains("Invoices?$skiptoken=page-2", StringComparison.Ordinal)) return Task.FromResult(Json(HttpStatusCode.OK, "{\"value\":[{\"DocEntry\":2,\"DocNum\":1002,\"CreationDate\":\"2026-07-02T00:00:00Z\",\"AttachmentEntry\":null}]}"));
            if (uri.Contains("Invoices?", StringComparison.Ordinal)) return Task.FromResult(Json(HttpStatusCode.OK, "{\"value\":[{\"DocEntry\":1,\"DocNum\":1001,\"CreationDate\":\"2026-07-01T00:00:00Z\",\"AttachmentEntry\":null}],\"@odata.nextLink\":\"Invoices?$skiptoken=page-2\"}"));
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"value\":[]}"));
        }
    }

    private sealed class ExpiringSessionHandler : HttpMessageHandler
    {
        public int LoginRequests { get; private set; }
        private int _documentRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Login"))
            {
                LoginRequests++;
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"SessionId\":\"session-" + LoginRequests + "\"}"));
            }
            _documentRequests++;
            if (_documentRequests == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"DocEntry\":4711,\"DocNum\":99,\"CardCode\":\"V100\",\"CardName\":\"Example Supplier GmbH\",\"NumAtCard\":\"RE-42\",\"DocDate\":\"2026-07-10\",\"DocTotal\":119.00,\"DocCurrency\":\"EUR\",\"AttachmentEntry\":12}"));
        }
    }

    private sealed class AttachmentWriteHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];
        private bool _linked;
        public AttachmentWriteHandler(bool initiallyLinked = false) => _linked = initiallyLinked;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, path, body));
            if (path.EndsWith("/Login")) return Json(HttpStatusCode.OK, "{\"SessionId\":\"session-1\"}");
            if (request.Method == HttpMethod.Post && path.EndsWith("/Attachments2")) return Json(HttpStatusCode.Created, "{\"AbsoluteEntry\":136}");
            if (request.Method == HttpMethod.Patch && path.EndsWith("/PurchaseInvoices(4711)")) { _linked = true; return new HttpResponseMessage(HttpStatusCode.NoContent); }
            if (path.EndsWith("/Attachments2(136)")) return Json(HttpStatusCode.OK, "{\"AbsoluteEntry\":136,\"Attachments2_Lines\":[{\"FileName\":\"freigegebener-test\",\"FileExtension\":\"pdf\"}]}");
            if (path.EndsWith("/PurchaseInvoices(4711)")) return Json(HttpStatusCode.OK, _linked ? "{\"DocEntry\":4711,\"DocNum\":99,\"CardCode\":\"V100\",\"CardName\":\"Example Supplier GmbH\",\"NumAtCard\":\"RE-42\",\"DocDate\":\"2026-07-10\",\"DocTotal\":119.00,\"DocCurrency\":\"EUR\",\"AttachmentEntry\":136}" : "{\"DocEntry\":4711,\"DocNum\":99,\"CardCode\":\"V100\",\"CardName\":\"Example Supplier GmbH\",\"NumAtCard\":\"RE-42\",\"DocDate\":\"2026-07-10\",\"DocTotal\":119.00,\"DocCurrency\":\"EUR\",\"AttachmentEntry\":null}");
            throw new InvalidOperationException("Unerwarteter SAP-Testaufruf: " + request.RequestUri);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
