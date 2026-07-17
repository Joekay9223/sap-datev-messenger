using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class OpenAiInvoiceDocumentInterpreterTests
{
    [Fact]
    public void Named_http_client_does_not_override_the_configured_document_timeout()
    {
        using var client = new HttpClient();

        Program.ConfigureOpenAiInvoiceDocumentClient(client);

        Assert.Equal(new Uri("https://api.openai.com/v1/"), client.BaseAddress);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void Sends_complete_pdf_with_strict_schema_and_parses_incoming_invoice()
    {
        string? requestJson = null;
        var handler = new DelegateHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse(WrapOutput(StructuredInvoice()));
        });
        var path = CreatePdfPlaceholder();
        try
        {
            var interpreter = CreateInterpreter(handler);

            var facts = interpreter.Extract(path, DocumentDirection.Incoming);

            Assert.Equal("R-EXAMPLE-001", facts.InvoiceNumber);
            Assert.Equal("Example Supplier GmbH", facts.BusinessPartnerName);
            Assert.Equal("DE000000001", facts.VatId);
            Assert.Equal(544.03m, facts.GrossAmount);
            Assert.Equal("EUR", facts.Currency);
            Assert.Equal(new DateOnly(2026, 7, 13), facts.InvoiceDate);
            Assert.True(facts.IsInvoice);
            Assert.True(facts.UsedOcr);
            Assert.False(facts.IsDocumentQualityUncertain);

            using var request = JsonDocument.Parse(requestJson!);
            Assert.False(request.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal("gpt-5.6-terra", request.RootElement.GetProperty("model").GetString());
            Assert.Equal("high", request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            var content = request.RootElement.GetProperty("input")[0].GetProperty("content");
            var file = content[0];
            Assert.Equal("input_file", file.GetProperty("type").GetString());
            Assert.Equal("high", file.GetProperty("detail").GetString());
            Assert.StartsWith("data:application/pdf;base64,", file.GetProperty("file_data").GetString());
            var prompt = content[1].GetProperty("text").GetString();
            Assert.Contains("Eingangsbeleg", prompt);
            Assert.DoesNotContain("SAP-Soll", prompt);
            var format = request.RootElement.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.True(format.GetProperty("strict").GetBoolean());
            Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Selects_recipient_as_business_partner_for_outgoing_invoice()
    {
        var facts = OpenAiInvoiceDocumentInterpreter.ParseStructuredResult(
            StructuredInvoice(),
            DocumentDirection.Outgoing);

        Assert.Equal("Example Company GmbH", facts.BusinessPartnerName);
        Assert.Equal("DE000000000", facts.VatId);
    }

    [Fact]
    public void Missing_visible_required_values_are_marked_uncertain_without_invention()
    {
        var result = JsonSerializer.Serialize(new
        {
            documentType = "invoice",
            invoiceNumber = (string?)null,
            issuerName = "Schwer lesbarer Lieferant",
            issuerVatId = (string?)null,
            recipientName = "Empfänger",
            recipientVatId = (string?)null,
            grossAmount = (decimal?)null,
            currency = (string?)null,
            invoiceDate = (string?)null,
            taxRate = (decimal?)null,
            hasRequiredFieldConflicts = false,
            isDocumentQualityUncertain = true,
            transcription = "Schwer lesbarer Scan ohne sicher erkennbare Pflichtfelder."
        });

        var facts = OpenAiInvoiceDocumentInterpreter.ParseStructuredResult(result, DocumentDirection.Incoming);

        Assert.Equal(string.Empty, facts.InvoiceNumber);
        Assert.Equal(0m, facts.GrossAmount);
        Assert.Equal(DateOnly.MinValue, facts.InvoiceDate);
        Assert.True(facts.IsDocumentQualityUncertain);
    }

    [Fact]
    public void Rejects_non_schema_result_instead_of_falling_back_to_local_parser()
    {
        var incomplete = """{"documentType":"invoice","invoiceNumber":"R-1"}""";

        Assert.Throws<InvalidDataException>(() =>
            OpenAiInvoiceDocumentInterpreter.ParseStructuredResult(incomplete, DocumentDirection.Incoming));
    }

    [Fact]
    public void Does_not_call_openai_without_server_key()
    {
        var path = CreatePdfPlaceholder();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var interpreter = new OpenAiInvoiceDocumentInterpreter(
                new StubHttpClientFactory(new ThrowingHandler()),
                configuration,
                NullLogger<OpenAiInvoiceDocumentInterpreter>.Instance);

            var error = Assert.Throws<InvalidOperationException>(() => interpreter.Extract(path));
            Assert.Contains("nicht konfiguriert", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static OpenAiInvoiceDocumentInterpreter CreateInterpreter(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = "test-key",
            ["OpenAI:DocumentInterpretationModel"] = "gpt-5.6-terra",
            ["OpenAI:DocumentInterpretationReasoningEffort"] = "high",
            ["OpenAI:DocumentInterpretationPdfDetail"] = "high"
        }).Build();
        return new OpenAiInvoiceDocumentInterpreter(
            new StubHttpClientFactory(handler),
            configuration,
            NullLogger<OpenAiInvoiceDocumentInterpreter>.Instance);
    }

    private static string StructuredInvoice() => JsonSerializer.Serialize(new
    {
        documentType = "invoice",
        invoiceNumber = "R-EXAMPLE-001",
        issuerName = "Example Supplier GmbH",
        issuerVatId = "DE000000001",
        recipientName = "Example Company GmbH",
        recipientVatId = "DE000000000",
        grossAmount = 544.03m,
        currency = "EUR",
        invoiceDate = "2026-07-13",
        taxRate = 19m,
        hasRequiredFieldConflicts = false,
        isDocumentQualityUncertain = false,
        transcription = "RECHNUNG\nRechnungsnummer R-EXAMPLE-001\nGesamtbetrag 544,03 EUR\nExample Supplier GmbH"
    });

    private static string WrapOutput(string structuredJson) => JsonSerializer.Serialize(new
    {
        output = new[]
        {
            new
            {
                content = new[]
                {
                    new { type = "output_text", text = structuredJson }
                }
            }
        }
    });

    private static string CreatePdfPlaceholder()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic test invoice"));
        return path;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new Xunit.Sdk.XunitException("OpenAI darf ohne API-Schlüssel nicht aufgerufen werden.");
    }
}
