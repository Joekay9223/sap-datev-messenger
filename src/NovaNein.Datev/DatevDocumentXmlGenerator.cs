using System.Xml;
using NovaNein.Domain;

namespace NovaNein.Datev;

public sealed record DatevDocumentManifest(
    DocumentDirection Direction,
    int DocNum,
    DateTimeOffset CreatedAt,
    string ClientNumber,
    string ClientName,
    string Description,
    string GeneratingSystem = "Novaline Workflow");

public static class DatevDocumentXmlGenerator
{
    public const string Namespace = "http://xml.datev.de/bedi/tps/document/v06.0";
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    public static string Create(DatevDocumentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.DocNum <= 0 || string.IsNullOrWhiteSpace(manifest.ClientNumber) || string.IsNullOrWhiteSpace(manifest.ClientName) || string.IsNullOrWhiteSpace(manifest.Description) || string.IsNullOrWhiteSpace(manifest.GeneratingSystem))
            throw new ArgumentException("DATEV-Dokumentmanifest enthält fehlende Pflichtfelder.", nameof(manifest));
        var prefix = manifest.Direction == DocumentDirection.Incoming ? "Eingangsrechnung" : "Ausgangsrechnung";
        var invoiceFile = $"{prefix}-{manifest.DocNum}.xml";
        var pdfFile = $"{prefix}-{manifest.DocNum}.pdf";
        var invoiceType = manifest.Direction == DocumentDirection.Incoming ? "Incoming" : "Outgoing";

        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false };
        using var text = new Utf8StringWriter();
        using var xml = XmlWriter.Create(text, settings);
        xml.WriteStartDocument();
        xml.WriteStartElement("archive", Namespace);
        xml.WriteAttributeString("xmlns", "xsi", null, XsiNamespace);
        xml.WriteAttributeString("xmlns", "xsd", null, XsdNamespace);
        xml.WriteAttributeString("xsi", "schemaLocation", XsiNamespace, $"{Namespace} Document_v060.xsd");
        xml.WriteAttributeString("version", "6.0");
        xml.WriteAttributeString("generatingSystem", manifest.GeneratingSystem.Trim());
        xml.WriteStartElement("header", Namespace);
        WriteElement(xml, "date", manifest.CreatedAt.LocalDateTime.ToString("yyyy-MM-ddTHH:mm:ss"));
        WriteElement(xml, "description", manifest.Description.Trim());
        WriteElement(xml, "clientNumber", manifest.ClientNumber.Trim());
        WriteElement(xml, "clientName", manifest.ClientName.Trim());
        xml.WriteEndElement();
        xml.WriteStartElement("content", Namespace);
        xml.WriteStartElement("document", Namespace);
        xml.WriteStartElement("extension", Namespace);
        xml.WriteAttributeString("xsi", "type", XsiNamespace, "Invoice");
        xml.WriteAttributeString("datafile", invoiceFile);
        xml.WriteStartElement("property", Namespace);
        xml.WriteAttributeString("value", invoiceType);
        xml.WriteAttributeString("key", "InvoiceType");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("extension", Namespace);
        xml.WriteAttributeString("xsi", "type", XsiNamespace, "File");
        xml.WriteAttributeString("name", pdfFile);
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
        xml.Flush();
        return text.ToString();
    }

    private static void WriteElement(XmlWriter xml, string localName, string value)
    {
        xml.WriteStartElement(localName, Namespace);
        xml.WriteString(value);
        xml.WriteEndElement();
    }
}
