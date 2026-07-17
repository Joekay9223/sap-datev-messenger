using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed record ExtractedInvoiceLine(
    int LineNumber,
    string Description,
    decimal NetAmount,
    decimal TaxAmount,
    decimal? TaxRate,
    string? SuggestedAccount,
    string? SuggestedTaxCode,
    bool LooksLikeGoods);

public sealed record ExtractedTaxSummary(decimal NetAmount, decimal TaxAmount, decimal? TaxRate);

public sealed record ExtractedInvoiceFacts(
    string InvoiceNumber,
    string BusinessPartnerName,
    string? VatId,
    decimal GrossAmount,
    string Currency,
    DateOnly InvoiceDate,
    bool IsInvoice,
    bool HasRequiredFieldConflicts,
    bool IsDocumentQualityUncertain,
    string Text,
    decimal? TaxRate = null,
    bool HasReadableDocumentContent = true,
    bool UsedOcr = true,
    string DocumentType = "invoice",
    string IssuerName = "",
    string? IssuerVatId = null,
    string RecipientName = "",
    string? RecipientVatId = null,
    string? SupplierTaxNumber = null,
    string? SupplierIban = null,
    string? SupplierStreet = null,
    string? SupplierPostalCode = null,
    string? SupplierCity = null,
    DateOnly? ServiceDate = null,
    DateOnly? DueDate = null,
    decimal? NetAmount = null,
    decimal? TaxAmount = null,
    IReadOnlyList<ExtractedTaxSummary>? Taxes = null,
    IReadOnlyList<ExtractedInvoiceLine>? Lines = null,
    bool HasPurchaseOrderReference = false,
    bool HasGoodsCharacteristics = false,
    bool IsReverseCharge = false,
    string? SuggestedAccount = null,
    string? SuggestedTaxCode = null,
    string? CodingReason = null);

public interface IPdfInvoiceTextExtractor
{
    ExtractedInvoiceFacts Extract(string path, DocumentDirection? direction = null);
}

/// <summary>
/// Übergibt die originale PDF direkt an OpenAI und erhält OCR und fachliche
/// Rechnungsinterpretation gemeinsam als strikt schemafestes Ergebnis. SAP-
/// Sollwerte werden bewusst nicht übertragen; der Abgleich erfolgt danach lokal.
/// </summary>
public sealed class OpenAiInvoiceDocumentInterpreter(
    IHttpClientFactory clientFactory,
    IConfiguration configuration,
    ILogger<OpenAiInvoiceDocumentInterpreter> logger) : IPdfInvoiceTextExtractor
{
    private const string ClientName = "OpenAiInvoiceDocument";

    public ExtractedInvoiceFacts Extract(string path, DocumentDirection? direction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Die PDF für die OpenAI-Dokumentinterpretation fehlt.", path);

        var key = configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Die OpenAI-Dokumentinterpretation ist nicht konfiguriert.");

        var file = File.ReadAllBytes(path);
        var maximumBytes = Math.Clamp(
            configuration.GetValue("OpenAI:DocumentInterpretationMaximumPdfBytes", 20 * 1024 * 1024),
            1 * 1024 * 1024,
            50 * 1024 * 1024);
        if (file.Length == 0 || file.Length > maximumBytes)
            throw new InvalidDataException($"Die PDF-Größe von {file.Length} Bytes ist für die OpenAI-Dokumentinterpretation unzulässig.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(
            JsonSerializer.Serialize(CreateRequest(file, direction)),
            Encoding.UTF8,
            "application/json");

        var timeoutSeconds = Math.Clamp(
            configuration.GetValue("OpenAI:DocumentInterpretationTimeoutSeconds", 180),
            30,
            300);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var response = clientFactory.CreateClient(ClientName)
                .SendAsync(request, timeout.Token)
                .GetAwaiter()
                .GetResult();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OpenAI-Dokumentinterpretation für {FileName} antwortete mit HTTP {StatusCode}.",
                    Path.GetFileName(path),
                    (int)response.StatusCode);
                throw new HttpRequestException(
                    $"OpenAI-Dokumentinterpretation antwortete mit HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            using var responseStream = response.Content.ReadAsStreamAsync(timeout.Token).GetAwaiter().GetResult();
            using var responseJson = JsonDocument.Parse(responseStream);
            var outputText = ReadOutputText(responseJson.RootElement)
                ?? throw new InvalidDataException("OpenAI hat kein strukturiertes Dokumentergebnis geliefert.");
            var result = ParseStructuredResult(outputText, direction);
            logger.LogInformation(
                "PDF {FileName} wurde vollständig durch OpenAI interpretiert ({Direction}).",
                Path.GetFileName(path),
                direction?.ToString() ?? "Unknown");
            return result;
        }
        catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("Zeitüberschreitung bei der OpenAI-Dokumentinterpretation.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("OpenAI hat ein ungültiges strukturiertes Dokumentergebnis geliefert.", exception);
        }
    }

    private object CreateRequest(byte[] file, DocumentDirection? direction)
    {
        var directionInstruction = direction switch
        {
            DocumentDirection.Incoming => "Der Beleg wird als Eingangsbeleg geprüft. issuerName/issuerVatId müssen den Lieferanten und recipientName/recipientVatId den Rechnungsempfänger enthalten.",
            DocumentDirection.Outgoing => "Der Beleg wird als Ausgangsbeleg geprüft. issuerName/issuerVatId müssen den Rechnungsaussteller und recipientName/recipientVatId den Kunden enthalten.",
            _ => "Die Belegseite ist noch unbekannt. Erfasse Rechnungsaussteller und Rechnungsempfänger getrennt und ohne Annahmen."
        };

        return new
        {
            model = configuration["OpenAI:DocumentInterpretationModel"]
                ?? configuration["OpenAI:Model"]
                ?? "gpt-5.6",
            store = false,
            reasoning = new
            {
                effort = NormalizeReasoningEffort(configuration["OpenAI:DocumentInterpretationReasoningEffort"])
            },
            max_output_tokens = Math.Clamp(
                configuration.GetValue("OpenAI:DocumentInterpretationMaximumOutputTokens", 16_000),
                2_000,
                50_000),
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_file",
                            filename = "rechnung.pdf",
                            file_data = "data:application/pdf;base64," + Convert.ToBase64String(file),
                            detail = NormalizePdfDetail(configuration["OpenAI:DocumentInterpretationPdfDetail"])
                        },
                        new
                        {
                            type = "input_text",
                            text = """
                                Lies die angehängte PDF vollständig anhand des extrahierten PDF-Texts und aller sichtbaren Seitenbilder. Führe OCR und Rechnungsinterpretation in einem Schritt aus.

                                Erfasse ausschließlich Werte, die im Beleg tatsächlich sichtbar sind. Erfinde nichts und übernimm keine erwarteten Werte aus einem anderen System. Bei mehreren Rechnungsnummern ist invoiceNumber die eigentliche Nummer des Lieferanten- bzw. Kundenbelegs, nicht Kunden-, Bestell-, SAP-, Auftrags-, Lieferschein- oder interne Archivnummer. Präfixe wie RE, RG, R, INV oder Invoice No. gehören zur sichtbaren Rechnungsnummer und dürfen erhalten bleiben.

                                grossAmount ist der zu zahlende Gesamt-/Bruttobetrag einschließlich Steuer, nicht Netto-, Positions-, Steuer- oder Zwischensumme. Bei Gutschriften darf der Betrag negativ sein. invoiceDate, serviceDate und dueDate verwenden yyyy-MM-dd. currency ist der ISO-Code, zum Beispiel EUR. documentType ist invoice, credit_note oder other. transcription enthält den vollständigen sichtbaren Belegtext in sinnvoller Lesereihenfolge.

                                Erfasse Kostenpositionen getrennt. lineNumber beginnt bei 1. line.netAmount und line.taxAmount sind die sichtbaren Beträge dieser Position. suggestedAccount und suggestedTaxCode sind ausschließlich fachliche Vorschläge aus dem Belegtext; sie dürfen null sein und werden vor einer SAP-Buchung gegen Historie und Stammdaten geprüft. codingReason erklärt den Vorschlag kurz.

                                hasGoodsCharacteristics und line.looksLikeGoods bedeuten nicht allgemein „physischer Gegenstand“, sondern ausschließlich: Die Position ist bei der Beispielorganisation voraussichtlich bestandsgeführt und muss artikelgenau in SAP gebucht werden. Dazu zählen inventory-managed goods and packaging materials. Betriebsmittel, Hautschutz, Reinigungsmittel, Bürobedarf, Werkzeuge, Ersatzteile, Fracht und sonstige Kostenpositionen sind false, auch wenn Menge, Artikelnummer oder eine Online-Bestellnummer sichtbar sind.

                                hasPurchaseOrderReference ist nur dann true, wenn der Beleg für bestandsgeführte Rohstoffe oder Verpackungsmaterial erkennbar auf eine Bestellung oder einen Wareneingang verweist. Eine gewöhnliche Shop-, Auftrags- oder Bestellnummer auf einer Kostenrechnung ist kein Grund für true. isReverseCharge ist nur bei einem eindeutigen Reverse-Charge-/§13b-Hinweis true.

                                Setze hasRequiredFieldConflicts nur dann auf true, wenn der Beleg selbst mehrere widersprüchliche Werte für dasselbe Pflichtfeld enthält. Setze isDocumentQualityUncertain auf true, wenn wichtige Zeichen oder Felder wegen Scanqualität, Überlagerung oder unklarer Zuordnung nicht zuverlässig lesbar sind. Nicht vorhandene optionale Werte müssen null sein.

                                """ + directionInstruction
                        }
                    }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "invoice_document_interpretation",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            documentType = new { type = "string", @enum = new[] { "invoice", "credit_note", "other" } },
                            invoiceNumber = new { type = new[] { "string", "null" } },
                            issuerName = new { type = new[] { "string", "null" } },
                            issuerVatId = new { type = new[] { "string", "null" } },
                            recipientName = new { type = new[] { "string", "null" } },
                            recipientVatId = new { type = new[] { "string", "null" } },
                            supplierTaxNumber = new { type = new[] { "string", "null" } },
                            supplierIban = new { type = new[] { "string", "null" } },
                            supplierStreet = new { type = new[] { "string", "null" } },
                            supplierPostalCode = new { type = new[] { "string", "null" } },
                            supplierCity = new { type = new[] { "string", "null" } },
                            grossAmount = new { type = new[] { "number", "null" } },
                            netAmount = new { type = new[] { "number", "null" } },
                            taxAmount = new { type = new[] { "number", "null" } },
                            currency = new { type = new[] { "string", "null" } },
                            invoiceDate = new { type = new[] { "string", "null" } },
                            serviceDate = new { type = new[] { "string", "null" } },
                            dueDate = new { type = new[] { "string", "null" } },
                            taxRate = new { type = new[] { "number", "null" } },
                            taxes = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        netAmount = new { type = "number" },
                                        taxAmount = new { type = "number" },
                                        taxRate = new { type = new[] { "number", "null" } }
                                    },
                                    required = new[] { "netAmount", "taxAmount", "taxRate" }
                                }
                            },
                            lines = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        lineNumber = new { type = "integer" },
                                        description = new { type = "string" },
                                        netAmount = new { type = "number" },
                                        taxAmount = new { type = "number" },
                                        taxRate = new { type = new[] { "number", "null" } },
                                        suggestedAccount = new { type = new[] { "string", "null" } },
                                        suggestedTaxCode = new { type = new[] { "string", "null" } },
                                        looksLikeGoods = new { type = "boolean" }
                                    },
                                    required = new[]
                                    {
                                        "lineNumber", "description", "netAmount", "taxAmount", "taxRate",
                                        "suggestedAccount", "suggestedTaxCode", "looksLikeGoods"
                                    }
                                }
                            },
                            hasPurchaseOrderReference = new { type = "boolean" },
                            hasGoodsCharacteristics = new { type = "boolean" },
                            isReverseCharge = new { type = "boolean" },
                            suggestedAccount = new { type = new[] { "string", "null" } },
                            suggestedTaxCode = new { type = new[] { "string", "null" } },
                            codingReason = new { type = new[] { "string", "null" } },
                            hasRequiredFieldConflicts = new { type = "boolean" },
                            isDocumentQualityUncertain = new { type = "boolean" },
                            transcription = new { type = "string" }
                        },
                        required = new[]
                        {
                            "documentType", "invoiceNumber", "issuerName", "issuerVatId",
                            "recipientName", "recipientVatId", "supplierTaxNumber", "supplierIban",
                            "supplierStreet", "supplierPostalCode", "supplierCity",
                            "grossAmount", "netAmount", "taxAmount", "currency",
                            "invoiceDate", "serviceDate", "dueDate", "taxRate", "taxes", "lines",
                            "hasPurchaseOrderReference", "hasGoodsCharacteristics", "isReverseCharge",
                            "suggestedAccount", "suggestedTaxCode", "codingReason", "hasRequiredFieldConflicts",
                            "isDocumentQualityUncertain", "transcription"
                        }
                    }
                }
            }
        };
    }

    internal static ExtractedInvoiceFacts ParseStructuredResult(string json, DocumentDirection? direction)
    {
        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Das OpenAI-Dokumentergebnis ist kein JSON-Objekt.");

        var documentType = RequiredString(root, "documentType");
        if (documentType is not ("invoice" or "credit_note" or "other"))
            throw new InvalidDataException("OpenAI hat einen unbekannten Dokumenttyp geliefert.");

        var invoiceNumber = OptionalString(root, "invoiceNumber") ?? string.Empty;
        var issuerName = OptionalString(root, "issuerName") ?? string.Empty;
        var issuerVatId = OptionalString(root, "issuerVatId");
        var recipientName = OptionalString(root, "recipientName") ?? string.Empty;
        var recipientVatId = OptionalString(root, "recipientVatId");
        var supplierTaxNumber = OptionalStringIfPresent(root, "supplierTaxNumber");
        var supplierIban = OptionalStringIfPresent(root, "supplierIban");
        var supplierStreet = OptionalStringIfPresent(root, "supplierStreet");
        var supplierPostalCode = OptionalStringIfPresent(root, "supplierPostalCode");
        var supplierCity = OptionalStringIfPresent(root, "supplierCity");
        var grossAmount = OptionalDecimal(root, "grossAmount") ?? 0m;
        var netAmount = OptionalDecimalIfPresent(root, "netAmount");
        var taxAmount = OptionalDecimalIfPresent(root, "taxAmount");
        var currency = NormalizeCurrency(OptionalString(root, "currency"));
        var invoiceDateText = OptionalString(root, "invoiceDate");
        var invoiceDate = ParseDate(invoiceDateText) ?? DateOnly.MinValue;
        var serviceDate = ParseDate(OptionalStringIfPresent(root, "serviceDate"));
        var dueDate = ParseDate(OptionalStringIfPresent(root, "dueDate"));
        var taxRate = OptionalDecimal(root, "taxRate");
        if (taxRate is < 0m or > 100m) taxRate = null;
        var taxes = ParseTaxes(root);
        var lines = ParseLines(root);
        var hasPurchaseOrderReference = OptionalBoolean(root, "hasPurchaseOrderReference");
        var hasGoodsCharacteristics = OptionalBoolean(root, "hasGoodsCharacteristics")
            || lines.Any(line => line.LooksLikeGoods);
        var isReverseCharge = OptionalBoolean(root, "isReverseCharge");
        var suggestedAccount = OptionalStringIfPresent(root, "suggestedAccount");
        var suggestedTaxCode = OptionalStringIfPresent(root, "suggestedTaxCode");
        var codingReason = OptionalStringIfPresent(root, "codingReason");
        var conflicts = RequiredBoolean(root, "hasRequiredFieldConflicts");
        var uncertain = RequiredBoolean(root, "isDocumentQualityUncertain");
        var transcription = RequiredString(root, "transcription");

        var (partner, vatId) = direction switch
        {
            DocumentDirection.Outgoing => (recipientName, recipientVatId),
            _ => (issuerName, issuerVatId)
        };
        var hasReadableContent = transcription.Count(char.IsLetterOrDigit) >= 20
            || !string.IsNullOrWhiteSpace(invoiceNumber)
            || grossAmount != 0m;
        uncertain = uncertain
            || !hasReadableContent
            || string.IsNullOrWhiteSpace(invoiceNumber)
            || grossAmount == 0m
            || string.IsNullOrWhiteSpace(currency)
            || invoiceDate == DateOnly.MinValue
            || string.IsNullOrWhiteSpace(partner);

        return new ExtractedInvoiceFacts(
            invoiceNumber,
            partner,
            vatId,
            grossAmount,
            currency,
            invoiceDate,
            documentType == "invoice",
            conflicts,
            uncertain,
            transcription,
            taxRate,
            hasReadableContent,
            UsedOcr: true,
            DocumentType: documentType,
            IssuerName: issuerName,
            IssuerVatId: issuerVatId,
            RecipientName: recipientName,
            RecipientVatId: recipientVatId,
            SupplierTaxNumber: supplierTaxNumber,
            SupplierIban: supplierIban,
            SupplierStreet: supplierStreet,
            SupplierPostalCode: supplierPostalCode,
            SupplierCity: supplierCity,
            ServiceDate: serviceDate,
            DueDate: dueDate,
            NetAmount: netAmount,
            TaxAmount: taxAmount,
            Taxes: taxes,
            Lines: lines,
            HasPurchaseOrderReference: hasPurchaseOrderReference,
            HasGoodsCharacteristics: hasGoodsCharacteristics,
            IsReverseCharge: isReverseCharge,
            SuggestedAccount: suggestedAccount,
            SuggestedTaxCode: suggestedTaxCode,
            CodingReason: codingReason);
    }

    private static string? ReadOutputText(JsonElement payload)
    {
        if (!payload.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("type", out var type) || type.GetString() != "output_text") continue;
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) return text.GetString();
            }
        }
        return null;
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält kein gültiges Feld {propertyName}.");
        return value.GetString() ?? string.Empty;
    }

    private static string? OptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält das Feld {propertyName} nicht.");
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => NullIfWhiteSpace(value.GetString()),
            _ => throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält ein ungültiges Feld {propertyName}.")
        };
    }

    private static decimal? OptionalDecimal(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält das Feld {propertyName} nicht.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result)) return result;
        throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält ein ungültiges Feld {propertyName}.");
    }

    private static decimal? OptionalDecimalIfPresent(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result)) return result;
        throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält ein ungültiges Feld {propertyName}.");
    }

    private static string? OptionalStringIfPresent(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => NullIfWhiteSpace(value.GetString()),
            _ => throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält ein ungültiges Feld {propertyName}.")
        };
    }

    private static bool OptionalBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält ein ungültiges Feld {propertyName}.");
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyList<ExtractedTaxSummary> ParseTaxes(JsonElement root)
    {
        if (!root.TryGetProperty("taxes", out var values)) return [];
        if (values.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Das OpenAI-Dokumentergebnis enthält ein ungültiges Feld taxes.");
        return values.EnumerateArray().Select(item => new ExtractedTaxSummary(
            RequiredDecimal(item, "netAmount"),
            RequiredDecimal(item, "taxAmount"),
            OptionalDecimalIfPresent(item, "taxRate"))).ToArray();
    }

    private static IReadOnlyList<ExtractedInvoiceLine> ParseLines(JsonElement root)
    {
        if (!root.TryGetProperty("lines", out var values)) return [];
        if (values.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Das OpenAI-Dokumentergebnis enthält ein ungültiges Feld lines.");
        return values.EnumerateArray().Select(item => new ExtractedInvoiceLine(
            RequiredInt(item, "lineNumber"),
            RequiredString(item, "description"),
            RequiredDecimal(item, "netAmount"),
            RequiredDecimal(item, "taxAmount"),
            OptionalDecimalIfPresent(item, "taxRate"),
            OptionalStringIfPresent(item, "suggestedAccount"),
            OptionalStringIfPresent(item, "suggestedTaxCode"),
            OptionalBoolean(item, "looksLikeGoods"))).ToArray();
    }

    private static decimal RequiredDecimal(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var result))
            return result;
        throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält kein gültiges Feld {propertyName}.");
    }

    private static int RequiredInt(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result))
            return result;
        throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält kein gültiges Feld {propertyName}.");
    }

    private static bool RequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Das OpenAI-Dokumentergebnis enthält kein gültiges Feld {propertyName}.");
        return value.GetBoolean();
    }

    private static string NormalizeReasoningEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" => value.Trim().ToLowerInvariant(),
        _ => "high"
    };

    private static string NormalizePdfDetail(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" or "auto" or "high" => value.Trim().ToLowerInvariant(),
        _ => "high"
    };

    private static string NormalizeCurrency(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "€" or "EURO" => "EUR",
        null => string.Empty,
        var normalized => normalized
    };

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
