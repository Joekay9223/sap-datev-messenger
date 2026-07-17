using System.IO.Compression;
using System.Text;
using NovaNein.Domain;

namespace NovaNein.Datev;

public sealed record InspectedDatevPackage(
    string Path,
    string Sha256,
    IReadOnlyList<string> Entries,
    string DocumentXml,
    string InvoiceXml);

public static class DatevPackageInspector
{
    private const long MaximumXmlBytes = 5 * 1024 * 1024;
    private const long MaximumPackageBytes = 30 * 1024 * 1024;

    public static InspectedDatevPackage Inspect(
        string packagePath,
        DocumentDirection direction,
        int docNum,
        string expectedSha256,
        IEnumerable<string>? xsdPaths = null)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !Path.IsPathFullyQualified(packagePath))
            throw new InvalidOperationException("Der DATEV-Paketpfad muss absolut sein.");
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Das DATEV-Paket wurde nicht gefunden.", packagePath);
        if (!string.Equals(Path.GetExtension(packagePath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Nur geschlossene DATEV-ZIP-Pakete dürfen geprüft werden.");
        if (docNum <= 0) throw new ArgumentOutOfRangeException(nameof(docNum));

        var file = new FileInfo(packagePath);
        if (file.Length <= 0 || file.Length > MaximumPackageBytes)
            throw new InvalidDataException("Das DATEV-ZIP besitzt eine ungültige Paketgröße.");

        string sha256;
        using (var stream = File.OpenRead(packagePath)) sha256 = DatevPackageRules.Sha256(stream);
        if (!string.Equals(NormalizeHash(expectedSha256), sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die DATEV-ZIP-Prüfsumme stimmt nicht mit dem Paketnachweis überein.");

        using var archive = ZipFile.OpenRead(packagePath);
        var entries = archive.Entries.Select(entry => entry.FullName).ToArray();
        var validation = DatevPackageRules.ValidateEntries(entries, direction, docNum);
        if (!validation.IsValid) throw new InvalidDataException(string.Join(" ", validation.Errors));

        var prefix = direction == DocumentDirection.Incoming ? "Eingangsrechnung" : "Ausgangsrechnung";
        var documentEntry = archive.GetEntry("document.xml") ?? throw new InvalidDataException("document.xml fehlt.");
        var invoiceEntry = archive.GetEntry($"{prefix}-{docNum}.xml") ?? throw new InvalidDataException("Die Rechnungs-XML fehlt.");
        var pdfEntry = archive.GetEntry($"{prefix}-{docNum}.pdf") ?? throw new InvalidDataException("Die Beleg-PDF fehlt.");
        if (documentEntry.Length > MaximumXmlBytes || invoiceEntry.Length > MaximumXmlBytes)
            throw new InvalidDataException("Eine DATEV-XML überschreitet die zulässige Prüfgröße.");
        if (pdfEntry.Length < 5 || pdfEntry.Length > DatevPackageGenerator.MaximumPdfBytes)
            throw new InvalidDataException("Die DATEV-PDF besitzt eine ungültige Größe.");

        var documentXml = ReadUtf8(documentEntry);
        var invoiceXml = ReadUtf8(invoiceEntry);
        DatevPackageRules.ValidateDocumentManifest(documentXml, direction, docNum);
        using (var pdf = pdfEntry.Open())
        {
            Span<byte> signature = stackalloc byte[5];
            if (pdf.Read(signature) != signature.Length || !signature.SequenceEqual("%PDF-"u8))
                throw new InvalidDataException("Die DATEV-PDF besitzt keine gültige PDF-Signatur.");
        }

        var schemas = (xsdPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (schemas.Length > 0)
        {
            DatevPackageGenerator.ValidateAgainstLocalXsds(documentXml, schemas);
            DatevPackageGenerator.ValidateAgainstLocalXsds(invoiceXml, schemas);
        }

        return new InspectedDatevPackage(packagePath, sha256, entries, documentXml, invoiceXml);
    }

    private static string ReadUtf8(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string NormalizeHash(string value)
    {
        var hash = new string((value ?? string.Empty).Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        if (hash.Length != 64) throw new ArgumentException("Die ZIP-SHA-256-Prüfsumme muss 64 Hexadezimalzeichen enthalten.", nameof(value));
        return hash;
    }
}
