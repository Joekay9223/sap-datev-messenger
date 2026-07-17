using System.Globalization;
using System.Text;
using System.Xml;

namespace NovaNein.Datev;

public sealed record DatevIncomingInvoice(
    int DocNum, string InvoiceId, DateOnly InvoiceDate, string SupplierName, string SupplierVatId,
    string SupplierStreet, string SupplierZip, string SupplierCity, string SupplierCountry,
    string BookingText, decimal NetAmount, decimal GrossAmount, decimal TaxAmount, decimal TaxRate,
    string Currency, string AccountNumber, string BuCode,
    string ClientVatId = "", string ClientTaxNumber = "", string ClientStreet = "", string ClientZip = "", string ClientCity = "", string ClientCountry = "DE", string ClientPartyId = "", string SupplierPartyId = "", string BusinessPartnerAccountNumber = "", string ItemDescription = "", decimal Quantity = 1m,
    IReadOnlyList<DatevBookingLine>? BookingLines = null, string ClientName = "",
    string InvoiceType = "Rechnung");

public sealed record DatevBookingLine(
    string AccountNumber, string BuCode, decimal NetAmount, decimal GrossAmount,
    decimal TaxAmount, decimal TaxRate, string Currency, string Description,
    decimal Quantity = 1m, string DebitCredit = "");

public static class DatevInvoiceXmlGenerator
{
    public const string Namespace = "http://xml.datev.de/bedi/tps/invoice/v060";
    public const int MaxInvoiceIdLength = 36;
    public static string CreateIncoming(DatevIncomingInvoice invoice) => Create(invoice, outgoing: false);

    /// <summary>
    /// Projects a source invoice number onto DATEV p10040.
    /// DATEV permits only ASCII letters, digits and a small set of special
    /// characters; source systems commonly include spaces or other separators.
    /// </summary>
    public static string NormalizeInvoiceId(string invoiceId)
    {
        if (string.IsNullOrWhiteSpace(invoiceId)) throw new ArgumentException("Die DATEV-Rechnungsnummer fehlt.", nameof(invoiceId));

        string source = invoiceId.Trim();
        var normalized = new StringBuilder(source.Length);
        bool replacementWritten = false;
        foreach (char character in source)
        {
            if (IsInvoiceIdCharacterAllowed(character))
            {
                normalized.Append(character);
                replacementWritten = false;
            }
            else if (normalized.Length > 0 && !replacementWritten)
            {
                normalized.Append('-');
                replacementWritten = true;
            }
        }

        if (normalized.Length == 0) throw new ArgumentException("Die DATEV-Rechnungsnummer enthält kein zulässiges Zeichen.", nameof(invoiceId));
        if (normalized.Length > MaxInvoiceIdLength) throw new ArgumentException($"Die DATEV-Rechnungsnummer darf höchstens {MaxInvoiceIdLength} Zeichen enthalten.", nameof(invoiceId));
        return normalized.ToString();
    }

    /// <summary>Creates the same DATEV invoice contract for an outgoing invoice.</summary>
    /// <remarks>
    /// The record is deliberately shared with the incoming path because the
    /// SAP snapshot contains the same accounting facts. For outgoing invoices
    /// the business-partner fields are written as the customer (invoice party)
    /// and the configured client fields as the seller (supplier party).
    /// </remarks>
    public static string CreateOutgoing(DatevIncomingInvoice invoice) => Create(invoice, outgoing: true);

    private static string Create(DatevIncomingInvoice invoice, bool outgoing)
    {
        string invoiceId = NormalizeInvoiceId(invoice.InvoiceId);
        if (invoice.DocNum <= 0 || string.IsNullOrWhiteSpace(invoice.SupplierName) || invoice.GrossAmount < invoice.NetAmount || string.IsNullOrWhiteSpace(invoice.Currency) || string.IsNullOrWhiteSpace(invoice.BuCode)) throw new ArgumentException("DATEV-Eingangsrechnung enthält fehlende oder ungültige Pflichtfelder.");
        if (invoice.InvoiceType is not ("Rechnung" or "Gutschrift/Rechnungskorrektur")) throw new ArgumentException("Der DATEV-Belegtyp ist nicht freigegeben.");
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false };
        using var text = new Utf8StringWriter(); using var xml = XmlWriter.Create(text, settings);
        xml.WriteStartDocument(); xml.WriteStartElement("invoice", Namespace); xml.WriteAttributeString("description", "DATEV Import invoices"); xml.WriteAttributeString("version", "6.0"); xml.WriteAttributeString("generator_info", "Novaline IT GmbH"); xml.WriteAttributeString("generating_system", "Novaline Workflow"); xml.WriteAttributeString("xml_data", "Kopie nur zur Verbuchung berechtigt nicht zum Vorsteuerabzug");
        Write(xml, "invoice_info", ("invoice_date", invoice.InvoiceDate.ToString("yyyy-MM-dd")), ("invoice_type", invoice.InvoiceType), ("delivery_date", invoice.InvoiceDate.ToString("yyyy-MM-dd")), ("invoice_id", invoiceId));
        Write(xml, "accounting_info", ("booking_text", invoice.BookingText));
        if (outgoing)
        {
            xml.WriteStartElement("invoice_party"); WriteAttributes(xml, ("vat_id", invoice.SupplierVatId)); Write(xml, "address", ("name", invoice.SupplierName), ("street", invoice.SupplierStreet), ("zip", invoice.SupplierZip), ("city", invoice.SupplierCity), ("country", invoice.SupplierCountry), ("party_id", invoice.SupplierPartyId)); Write(xml, "booking_info_bp", ("bp_account_no", invoice.BusinessPartnerAccountNumber)); xml.WriteEndElement();
            xml.WriteStartElement("supplier_party"); WriteAttributes(xml, ("vat_id", invoice.ClientVatId), ("tax_no", invoice.ClientTaxNumber)); Write(xml, "address", ("name", invoice.ClientName), ("street", invoice.ClientStreet), ("zip", invoice.ClientZip), ("city", invoice.ClientCity), ("country", invoice.ClientCountry), ("party_id", invoice.ClientPartyId)); xml.WriteEndElement();
        }
        else
        {
            xml.WriteStartElement("invoice_party"); WriteAttributes(xml, ("vat_id", invoice.ClientVatId), ("tax_no", invoice.ClientTaxNumber)); Write(xml, "address", ("name", invoice.ClientName), ("street", invoice.ClientStreet), ("zip", invoice.ClientZip), ("city", invoice.ClientCity), ("country", invoice.ClientCountry), ("party_id", invoice.ClientPartyId)); xml.WriteEndElement();
            xml.WriteStartElement("supplier_party"); WriteAttributes(xml, ("vat_id", invoice.SupplierVatId)); Write(xml, "address", ("name", invoice.SupplierName), ("street", invoice.SupplierStreet), ("zip", invoice.SupplierZip), ("city", invoice.SupplierCity), ("country", invoice.SupplierCountry), ("party_id", invoice.SupplierPartyId)); Write(xml, "booking_info_bp", ("bp_account_no", invoice.BusinessPartnerAccountNumber)); xml.WriteEndElement();
        }
		IReadOnlyList<DatevBookingLine> sourceLines = invoice.BookingLines is { Count: > 0 }
            ? invoice.BookingLines
            : new[] { new DatevBookingLine(invoice.AccountNumber, invoice.BuCode, invoice.NetAmount, invoice.GrossAmount, invoice.TaxAmount, invoice.TaxRate, invoice.Currency, string.IsNullOrWhiteSpace(invoice.ItemDescription) ? invoice.BookingText : invoice.ItemDescription, invoice.Quantity) };
		IReadOnlyList<DatevBookingLine> lines = sourceLines.Where(line => line.NetAmount != 0m || line.GrossAmount != 0m || line.TaxAmount != 0m).ToArray();
		if (lines.Count == 0) throw new ArgumentException("DATEV-Rechnung enthält keine betragswirksame Position.");
        foreach (var line in lines)
        {
            xml.WriteStartElement("invoice_item_list");
            var quantity = line.Quantity == 0m ? 1m : line.Quantity;
            WriteAttributes(xml, ("description_short", ShortDescription(string.IsNullOrWhiteSpace(line.Description) ? invoice.BookingText : line.Description)), ("quantity", Amount(quantity)), ("net_product_price", line.NetAmount == 0m ? string.Empty : Amount3(line.NetAmount / quantity)));
			Write(xml, "price_line_amount", ("net_price_line_amount", Amount(line.NetAmount)), ("gross_price_line_amount", Amount(line.GrossAmount)), ("tax_amount", OptionalNonZeroAmount(line.TaxAmount)), ("tax", Amount(line.TaxRate)), ("currency", line.Currency));
            Write(xml, "accounting_info", ("account_no", line.AccountNumber), ("bu_code", line.BuCode), ("booking_text", invoice.BookingText));
            xml.WriteEndElement();
        }
        xml.WriteStartElement("total_amount");
        xml.WriteAttributeString("net_total_amount", Amount(invoice.NetAmount));
        xml.WriteAttributeString("total_gross_amount_excluding_third-party_collection", Amount(invoice.GrossAmount));
        xml.WriteAttributeString("currency", invoice.Currency);
        foreach (var line in lines)
			Write(xml, "tax_line", ("net_price_line_amount", Amount(line.NetAmount)), ("gross_price_line_amount", Amount(line.GrossAmount)), ("tax", Amount(line.TaxRate)), ("tax_amount", OptionalNonZeroAmount(line.TaxAmount)), ("currency", line.Currency));
        xml.WriteEndElement();
        xml.WriteEndElement(); xml.WriteEndDocument(); xml.Flush(); return text.ToString();
    }
    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
	private static string OptionalNonZeroAmount(decimal value) => value == 0m ? string.Empty : Amount(value);
    private static string Amount3(decimal value) => value.ToString("0.000", CultureInfo.InvariantCulture);
    // DATEV-Typ p10009 (Bezeichnung 040) erlaubt exakt höchstens 40 Zeichen.
    // Die XSD-Dokumentation sieht ausdrücklich vor, nur die ersten 40 Stellen zu verwenden.
    private static string ShortDescription(string value)
    {
        var normalized = value.Trim();
        return normalized.Length <= 40 ? normalized : normalized[..40];
    }
    private static bool IsInvoiceIdCharacterAllowed(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '$' or '%' or '&' or '*' or '+' or '-' or '/';
    private static void Write(XmlWriter xml, string element, params (string Name, string Value)[] attributes) { xml.WriteStartElement(element); WriteAttributes(xml, attributes); xml.WriteEndElement(); }
    private static void WriteAttributes(XmlWriter xml, params (string Name, string Value)[] attributes) { foreach (var (name, value) in attributes) if (!string.IsNullOrWhiteSpace(value)) xml.WriteAttributeString(name, value); }
}
