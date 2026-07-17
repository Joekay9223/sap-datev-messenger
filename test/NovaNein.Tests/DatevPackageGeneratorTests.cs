using System.IO.Compression;
using NovaNein.Datev;
using NovaNein.Domain;

namespace NovaNein.Tests;

public sealed class DatevPackageGeneratorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-datev-{Guid.NewGuid():N}");
    private readonly DatevPackageGenerator _generator = new();

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void Creates_closed_valid_package_once()
    {
        var created = _generator.Create(Request(), _directory);
        Assert.True(File.Exists(created.Path));
        Assert.Equal(64, created.Sha256.Length);
        using var archive = ZipFile.OpenRead(created.Path);
        var validation = DatevPackageRules.ValidateEntries(archive.Entries.Select(x => x.FullName), DocumentDirection.Incoming, 42);
        Assert.True(validation.IsValid);
        using (var documentReader = new StreamReader(archive.GetEntry("document.xml")!.Open()))
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", documentReader.ReadLine());
        Assert.Throws<IOException>(() => _generator.Create(Request(), _directory));
    }

    [Fact]
    public void Inspector_rechecks_hash_manifest_pdf_and_exact_three_entries()
    {
        var created = _generator.Create(Request(), _directory);
        var inspected = DatevPackageInspector.Inspect(created.Path, DocumentDirection.Incoming, 42, created.Sha256);
        Assert.Equal(created.Sha256, inspected.Sha256);
        Assert.Equal(["document.xml", "Eingangsrechnung-42.pdf", "Eingangsrechnung-42.xml"], inspected.Entries.OrderBy(x => x));
        Assert.Throws<InvalidDataException>(() => DatevPackageInspector.Inspect(created.Path, DocumentDirection.Incoming, 42, new string('F', 64)));
    }

    [Fact]
    public void Inspector_rejects_extra_metadata_even_with_matching_hash()
    {
        var created = _generator.Create(Request(), _directory);
        using (var archive = ZipFile.Open(created.Path, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry("README.txt").Open())) writer.Write("darf nicht übertragen werden");
        using var input = File.OpenRead(created.Path);
        var tamperedHash = DatevPackageRules.Sha256(input);
        Assert.Throws<InvalidDataException>(() => DatevPackageInspector.Inspect(created.Path, DocumentDirection.Incoming, 42, tamperedHash));
    }

    [Fact]
    public void Rejects_invalid_or_unsafe_input_before_zip_creation()
    {
        Assert.Throws<InvalidDataException>(() => _generator.Create(Request() with { PdfContent = "not a PDF"u8.ToArray() }, _directory));
        Assert.Throws<InvalidDataException>(() => _generator.Create(Request() with { InvoiceXml = "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x>&e;</x>" }, _directory));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Rejects_a_pdf_above_the_datev_single_file_limit()
    {
        var oversized = new byte[DatevPackageGenerator.MaximumPdfBytes + 1];
        "%PDF-"u8.CopyTo(oversized);
        Assert.Throws<InvalidDataException>(() => _generator.Create(Request() with { PdfContent = oversized }, _directory));
    }

    [Fact]
    public void Rejects_document_manifest_with_wrong_file_reference()
    {
        var valid = DatevDocumentXmlGenerator.Create(new DatevDocumentManifest(DocumentDirection.Incoming, 42, new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero), "12345", "Testmandant", "Eingangsrechnung"));
        var wrong = valid.Replace("Eingangsrechnung-42.pdf", "Eingangsrechnung-99.pdf", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => _generator.Create(Request() with { DocumentXml = wrong }, _directory));
    }

    [Fact]
    public void Uses_the_approved_novaline_generating_system_by_default()
    {
        var xml = DatevDocumentXmlGenerator.Create(new DatevDocumentManifest(
            DocumentDirection.Incoming,
            42,
            new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero),
            "00000",
            "Example Company GmbH",
            "Eingangsrechnung"));

        Assert.Contains("generatingSystem=\"Novaline Workflow\"", xml);
        Assert.Contains("<clientName>Example Company GmbH &amp; Co. KG</clientName>", xml);
    }

    [Fact]
    public void Creates_package_from_typed_incoming_invoice()
    {
        var invoice = new DatevIncomingInvoice(42, "FIXTURE-42", new DateOnly(2026, 7, 9), "Testlieferant GmbH", "DE000000001", "Teststraße 1", "12345", "Teststadt", "DE", "Test", 100m, 119m, 19m, 19m, "EUR", "1600", "9");
        var manifest = new DatevDocumentManifest(DocumentDirection.Incoming, 42, new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero), "12345", "Testmandant", "Eingangsrechnung");
        var package = _generator.CreateIncoming(invoice, manifest, "%PDF-1.7\nfixture"u8.ToArray(), _directory);
        using var archive = ZipFile.OpenRead(package.Path);
        var entry = archive.GetEntry("Eingangsrechnung-42.xml")!;
        using var reader = new StreamReader(entry.Open());
        Assert.Contains("bu_code=\"9\"", reader.ReadToEnd());
    }


    [Fact]
    public void Blocks_path_traversal_in_watchfolder_target_name()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "source.zip");
        File.WriteAllBytes(source, "PK"u8.ToArray());
        var transfer = new AtomicWatchfolderTransfer();
        Assert.Throws<InvalidOperationException>(() => transfer.MoveCompletedPackage(source, _directory, "../escape.zip"));
    }

    [Fact]
    public void Validates_xml_against_configured_local_xsd()
    {
        Directory.CreateDirectory(_directory);
        var xsd = Path.Combine(_directory, "simple.xsd");
        File.WriteAllText(xsd, """<?xml version="1.0"?><xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="invoice" type="xs:string"/></xs:schema>""");
        DatevPackageGenerator.ValidateAgainstLocalXsds("<invoice>ok</invoice>", [xsd]);
        Assert.Throws<InvalidDataException>(() => DatevPackageGenerator.ValidateAgainstLocalXsds("<wrong/>", [xsd]));
    }

    private static DatevPackageRequest Request() => new(
        DocumentDirection.Incoming,
        42,
        DatevDocumentXmlGenerator.Create(new DatevDocumentManifest(DocumentDirection.Incoming, 42, new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero), "12345", "Testmandant", "Eingangsrechnung")),
        "<invoice><number>42</number></invoice>",
        "%PDF-1.7\nsynthetic fixture"u8.ToArray());
}
