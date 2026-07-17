namespace NovaNein.Domain;

public sealed record InvoiceFacts(
    string InvoiceNumber,
    string BusinessPartnerName,
    string? VatId,
    decimal GrossAmount,
    string Currency,
    DateOnly InvoiceDate,
    bool IsInvoice,
    bool HasRequiredFieldConflicts,
    bool IsDocumentQualityUncertain = false,
    bool HasReadableDocumentContent = true);

public sealed record ValidationResult(ReviewSignal Signal, IReadOnlyList<string> Reasons)
{
    public bool RequiresManualReview => Signal is ReviewSignal.Yellow or ReviewSignal.Red;
}

public static class InvoiceValidation
{
    private static readonly string[] InterchangeableInvoicePrefixes =
    [
        "RECHNUNGSNUMMER",
        "RECHNUNGSNR",
        "RECHNUNGNR",
        "RECHNUNG",
        "INVOICENUMBER",
        "INVOICENO",
        "INVOICE",
        "BELEGNUMMER",
        "BELEGNR",
        "BELEG",
        "RENR",
        "RGNR",
        "INVNR",
        "RE",
        "RG",
        "INV"
    ];

    public static ValidationResult Compare(InvoiceFacts sap, InvoiceFacts pdf, int allowedDateDifferenceDays = 31)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allowedDateDifferenceDays);
        var red = new List<string>();
        var yellow = new List<string>();

        if (!pdf.HasReadableDocumentContent)
            return new(ReviewSignal.Yellow, ["Die PDF konnte durch OpenAI nicht zuverlässig gelesen werden; manuelle Prüfung erforderlich."]);

        if (!pdf.IsInvoice)
        {
            if (pdf.IsDocumentQualityUncertain) yellow.Add("Der Rechnungstyp konnte wegen der Dokumentqualität nicht sicher erkannt werden.");
            else red.Add("Das PDF wurde nicht als Rechnung erkannt.");
        }
        if (pdf.HasRequiredFieldConflicts) red.Add("Das PDF enthält widersprüchliche Pflichtfelder.");
        if (string.IsNullOrWhiteSpace(pdf.InvoiceNumber) && pdf.IsDocumentQualityUncertain) yellow.Add("Die Rechnungsnummer konnte nicht sicher gelesen werden.");
        else if (!InvoiceNumbersMatch(sap.InvoiceNumber, pdf.InvoiceNumber)) red.Add("Die Rechnungsnummer stimmt nicht überein.");
        if (pdf.GrossAmount == 0m && sap.GrossAmount != 0m && pdf.IsDocumentQualityUncertain) yellow.Add("Der Bruttobetrag konnte nicht sicher gelesen werden.");
        else if (sap.GrossAmount != pdf.GrossAmount) red.Add("Der Bruttobetrag stimmt nicht überein.");
        if (string.IsNullOrWhiteSpace(pdf.Currency) && pdf.IsDocumentQualityUncertain) yellow.Add("Die Währung konnte nicht sicher gelesen werden.");
        else if (!string.Equals(Normalize(sap.Currency), Normalize(pdf.Currency), StringComparison.OrdinalIgnoreCase)) red.Add("Die Währung stimmt nicht überein.");

        var bothVatIdsPresent = !string.IsNullOrWhiteSpace(sap.VatId) && !string.IsNullOrWhiteSpace(pdf.VatId);
        if (bothVatIdsPresent)
        {
            if (!string.Equals(NormalizeVatId(sap.VatId!), NormalizeVatId(pdf.VatId!), StringComparison.OrdinalIgnoreCase)) red.Add("Die USt-ID stimmt nicht überein.");
        }
        else if (!NamesMatch(sap.BusinessPartnerName, pdf.BusinessPartnerName)) yellow.Add("Der Geschäftspartnername ist nicht exakt zuordenbar.");

        if (pdf.InvoiceDate == DateOnly.MinValue && pdf.IsDocumentQualityUncertain) yellow.Add("Das Rechnungsdatum konnte nicht sicher gelesen werden.");
        else
        {
            var dateDifference = Math.Abs(sap.InvoiceDate.DayNumber - pdf.InvoiceDate.DayNumber);
            if (dateDifference > allowedDateDifferenceDays) red.Add("Das Rechnungsdatum liegt außerhalb der erlaubten Toleranz.");
            else if (dateDifference != 0) yellow.Add("Das Rechnungsdatum weicht innerhalb der erlaubten Toleranz ab.");
        }
        if (pdf.IsDocumentQualityUncertain) yellow.Add("Die Dokumentqualität oder OpenAI-Erkennung ist unsicher.");
        if (string.IsNullOrWhiteSpace(pdf.VatId) && !NamesMatch(sap.BusinessPartnerName, pdf.BusinessPartnerName)) yellow.Add("USt-ID fehlt; der Name kann nicht sicher zugeordnet werden.");

        return red.Count > 0 ? new(ReviewSignal.Red, red) : yellow.Count > 0 ? new(ReviewSignal.Yellow, yellow) : new(ReviewSignal.Green, []);
    }

    public static string NormalizeInvoiceNumber(string value)
    {
        var compact = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var firstDigit = compact.IndexOfAny("0123456789".ToCharArray());
        return firstDigit < 0 ? compact : compact[..firstDigit] + compact[firstDigit..].TrimStart('0');
    }

    public static bool InvoiceNumbersMatch(string left, string right)
    {
        var normalizedLeft = NormalizeInvoiceNumber(left);
        var normalizedRight = NormalizeInvoiceNumber(right);
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal)) return true;

        var comparableLeft = RemoveInterchangeablePrefix(normalizedLeft);
        var comparableRight = RemoveInterchangeablePrefix(normalizedRight);
        return comparableLeft.Length > 0
            && (string.Equals(comparableLeft, comparableRight, StringComparison.Ordinal)
                || MatchCommonOcrAmbiguities(comparableLeft, comparableRight));
    }

    private static bool MatchCommonOcrAmbiguities(string left, string right)
    {
        if (left.Length != right.Length || !left.Any(char.IsDigit) || !right.Any(char.IsDigit)) return false;
        var substitutions = 0;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] == right[index]) continue;
            if (!IsCommonOcrPair(left[index], right[index])) return false;
            substitutions++;
            if (substitutions > 2) return false;
        }
        return substitutions > 0;
    }

    private static bool IsCommonOcrPair(char left, char right)
    {
        static char Canonical(char value) => value switch
        {
            'O' => '0',
            'I' or 'L' => '1',
            'Z' => '2',
            'S' => '5',
            'G' => '6',
            'B' => '8',
            _ => value
        };
        return Canonical(left) == Canonical(right);
    }

    private static string RemoveInterchangeablePrefix(string value)
    {
        foreach (var prefix in InterchangeableInvoicePrefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length) continue;
            var remainder = value[prefix.Length..];
            if (!remainder.Any(char.IsDigit)) continue;
            return remainder.TrimStart('0') is { Length: > 0 } withoutZeros ? withoutZeros : "0";
        }
        return value;
    }
    private static string Normalize(string value) => (value ?? string.Empty).Trim();
    private static string NormalizeVatId(string value) => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
    private static bool NamesMatch(string left, string right) => string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.Ordinal);
    private static string NormalizeName(string value) => new string((value ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}
