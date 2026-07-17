using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using NovaNein.Domain;

namespace NovaNein.Datev;

public sealed record DatevPackageRequest(
    DocumentDirection Direction,
    int DocNum,
    string DocumentXml,
    string InvoiceXml,
    byte[] PdfContent);

public sealed record CreatedDatevPackage(string Path, string Sha256, IReadOnlyList<string> Entries);

public sealed class DatevPackageGenerator
{
    public const int MaximumPdfBytes = 20 * 1024 * 1024;
    public CreatedDatevPackage CreateIncoming(DatevIncomingInvoice invoice, string documentXml, byte[] pdfContent, string workingDirectory) =>
        Create(new DatevPackageRequest(DocumentDirection.Incoming, invoice.DocNum, documentXml, DatevInvoiceXmlGenerator.CreateIncoming(invoice), pdfContent), workingDirectory);

    public CreatedDatevPackage CreateIncoming(DatevIncomingInvoice invoice, DatevDocumentManifest manifest, byte[] pdfContent, string workingDirectory)
    {
        if (manifest.Direction != DocumentDirection.Incoming || manifest.DocNum != invoice.DocNum) throw new ArgumentException("DATEV-Manifest und Eingangsrechnung müssen dieselbe Richtung und DocNum besitzen.", nameof(manifest));
        return Create(new DatevPackageRequest(DocumentDirection.Incoming, invoice.DocNum, DatevDocumentXmlGenerator.Create(manifest), DatevInvoiceXmlGenerator.CreateIncoming(invoice), pdfContent), workingDirectory);
    }

    public CreatedDatevPackage Create(DatevPackageRequest request, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DocNum <= 0) throw new ArgumentOutOfRangeException(nameof(request.DocNum), "Die SAP-Dokumentnummer muss positiv sein.");
        if (!Path.IsPathFullyQualified(workingDirectory)) throw new InvalidOperationException("Der Paket-Arbeitsordner muss ein absoluter Pfad sein.");
        ValidateXml(request.DocumentXml, "document.xml");
        ValidateXml(request.InvoiceXml, "Rechnungs-XML");
        ValidatePdf(request.PdfContent);

        var prefix = request.Direction == DocumentDirection.Incoming ? "Eingangsrechnung" : "Ausgangsrechnung";
        var entries = new[] { "document.xml", $"{prefix}-{request.DocNum}.xml", $"{prefix}-{request.DocNum}.pdf" };
        var result = DatevPackageRules.ValidateEntries(entries, request.Direction, request.DocNum);
        if (!result.IsValid) throw new InvalidOperationException(string.Join(" ", result.Errors));
        DatevPackageRules.ValidateDocumentManifest(request.DocumentXml, request.Direction, request.DocNum);

        Directory.CreateDirectory(workingDirectory);
        var targetPath = Path.Combine(workingDirectory, $"{prefix}-{request.DocNum}.zip");
        if (File.Exists(targetPath)) throw new IOException("Für diesen SAP-Beleg wurde bereits ein DATEV-Paket erzeugt.");
        var temporaryPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteTextEntry(archive, entries[0], request.DocumentXml);
                WriteTextEntry(archive, entries[1], request.InvoiceXml);
                var pdf = archive.CreateEntry(entries[2], CompressionLevel.Optimal);
                using var pdfStream = pdf.Open();
                pdfStream.Write(request.PdfContent);
            }

            using (var verification = ZipFile.OpenRead(temporaryPath))
            {
                var verificationResult = DatevPackageRules.ValidateEntries(verification.Entries.Select(x => x.FullName), request.Direction, request.DocNum);
                if (!verificationResult.IsValid) throw new InvalidOperationException(string.Join(" ", verificationResult.Errors));
            }
            File.Move(temporaryPath, targetPath);
            using var package = File.OpenRead(targetPath);
            return new(targetPath, DatevPackageRules.Sha256(package), entries);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public static void ValidateAgainstLocalXsds(string xml, IEnumerable<string> xsdPaths)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new InvalidDataException("Die XML für die XSD-Prüfung ist leer.");
        var paths = (xsdPaths ?? throw new ArgumentNullException(nameof(xsdPaths))).Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("Mindestens eine lokale DATEV-XSD ist erforderlich.");
        if (paths.Any(path => !File.Exists(path))) throw new FileNotFoundException("Mindestens eine konfigurierte DATEV-XSD wurde nicht gefunden.");
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        foreach (var path in paths) schemas.Add(null, path);
        schemas.Compile();
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        settings.ValidationEventHandler += (_, eventData) => errors.Add(eventData.Message);
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read()) { }
        if (errors.Count > 0) throw new InvalidDataException("DATEV-XSD-Prüfung fehlgeschlagen: " + string.Join(" ", errors));
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void ValidatePdf(byte[] content)
    {
        if (content is null || content.Length < 5 || !content.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new InvalidDataException("Die Paket-PDF besitzt keine gültige PDF-Signatur.");
        if (content.Length > MaximumPdfBytes) throw new InvalidDataException("Die Paket-PDF überschreitet die DATEV-Grenze von 20 MB.");
    }

    private static void ValidateXml(string content, string label)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException($"{label} ist leer.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), settings);
            while (reader.Read()) { }
        }
        catch (XmlException ex) { throw new InvalidDataException($"{label} ist kein sicheres, wohlgeformtes XML.", ex); }
    }
}
