using NovaNein.Datev;
namespace NovaNein.Tests;
public class DatevInvoiceXmlGeneratorTests
{
 [Fact] public void Creates_required_incoming_xml_with_bu_code_9(){ var xml=DatevInvoiceXmlGenerator.CreateIncoming(new(80001,"FIXTURE-1",new DateOnly(2026,7,9),"Testlieferant GmbH","DE000000001","Teststraße 1","12345","Teststadt","DE","Test",100,119,19,19,"EUR","1600","9", BusinessPartnerAccountNumber:"70001")); Assert.Contains("bu_code=\"9\"",xml); Assert.Contains("invoice_info",xml); Assert.Contains("total_amount",xml); Assert.Contains("description=\"DATEV Import invoices\"", xml); Assert.Contains("version=\"6.0\"", xml); Assert.Contains("generator_info=\"Novaline IT GmbH\"", xml); Assert.Contains("generating_system=\"Novaline Workflow\"", xml); Assert.Contains("xml_data=\"Kopie nur zur Verbuchung berechtigt nicht zum Vorsteuerabzug\"", xml); Assert.Contains("invoice_type=\"Rechnung\"", xml); Assert.DoesNotContain("schemaLocation", xml); Assert.Contains("<invoice_party>", xml); Assert.Contains("<supplier_party vat_id=\"DE000000001\">", xml); Assert.Contains("<booking_info_bp", xml); Assert.Contains("description_short=\"Test\"", xml); var document = new System.Xml.XmlDocument(); document.LoadXml(xml); Assert.Equal(DatevInvoiceXmlGenerator.Namespace, document.DocumentElement!.NamespaceURI); var supplier=document.DocumentElement.SelectSingleNode("/*[local-name()='invoice']/*[local-name()='supplier_party']")!; Assert.Equal(DatevInvoiceXmlGenerator.Namespace, supplier.NamespaceURI); Assert.NotNull(supplier.SelectSingleNode("./*[local-name()='booking_info_bp']")); Assert.Null(document.DocumentElement.SelectSingleNode("./*[local-name()='booking_info_bp']")); }
 [Fact] public void Rejects_missing_bu_code(){ Assert.Throws<ArgumentException>(()=>DatevInvoiceXmlGenerator.CreateIncoming(new(1,"I",new DateOnly(2026,1,1),"S","","","","","","",1,1,0,0,"EUR","",""))); }
 [Fact] public void Normalizes_source_invoice_numbers_to_datev_p10040(){ Assert.Equal("FIE-83/IE", DatevInvoiceXmlGenerator.NormalizeInvoiceId(" FIE 83/IE ")); var xml = DatevInvoiceXmlGenerator.CreateIncoming(new(900458,"FIE 83/IE",new DateOnly(2026,7,8),"Testlieferant GmbH","IT000000001","Straße 1","12345","Ort","DE","Test",66600,66600,0,0,"EUR","3320","0",BusinessPartnerAccountNumber:"70001")); Assert.Contains("invoice_id=\"FIE-83/IE\"", xml); }
 [Fact] public void Rejects_invoice_numbers_longer_than_datev_limit(){ Assert.Throws<ArgumentException>(() => DatevInvoiceXmlGenerator.NormalizeInvoiceId(new string('A', DatevInvoiceXmlGenerator.MaxInvoiceIdLength + 1))); }
 [Fact]
 public void Limits_datev_short_description_to_the_xsd_maximum_of_40_characters()
 {
  const string description = "Lohnkosten für Auflockerung von 300 Krt a 10 KG Pfirsiche unter Zugabe von Reismehl.";
  var xml = DatevInvoiceXmlGenerator.CreateOutgoing(new(910810,"910810",new DateOnly(2026,7,14),"Example Supplier GmbH","DE000000001","Straße 1","12345","Ort","DE","Example Supplier",1520m,1808.80m,288.80m,19m,"EUR","8400","9",BookingLines:[new DatevBookingLine("8400","9",1520m,1808.80m,288.80m,19m,"EUR",description)]));
  var document = new System.Xml.XmlDocument(); document.LoadXml(xml);
  var value = document.SelectSingleNode("/*[local-name()='invoice']/*[local-name()='invoice_item_list']/@description_short")!.Value!;
  Assert.Equal(40, value.Length);
  Assert.Equal(description[..40], value);
 }
 [Fact] public void Creates_one_invoice_item_and_tax_line_for_each_sap_booking_line()
 {
  var lines = new[]
  {
   new DatevBookingLine("4930", "9", 100m, 119m, 19m, 19m, "EUR", "Büromaterial", 1m),
   new DatevBookingLine("4730", "9", 20m, 23.8m, 3.8m, 19m, "EUR", "Fracht", 1m)
  };
  var xml = DatevInvoiceXmlGenerator.CreateIncoming(new(80002,"FIXTURE-2",new DateOnly(2026,7,9),"Testlieferant GmbH","DE000000001","Teststraße 1","12345","Teststadt","DE","Test",120m,142.8m,22.8m,19m,"EUR","4930","9", BookingLines: lines));
  var document = new System.Xml.XmlDocument(); document.LoadXml(xml);
  var items = document.SelectNodes("/*[local-name()='invoice']/*[local-name()='invoice_item_list']");
  var taxLines = document.SelectNodes("/*[local-name()='invoice']/*[local-name()='total_amount']/*[local-name()='tax_line']");
  Assert.Equal(2, items!.Count);
  Assert.Equal(2, taxLines!.Count);
  Assert.Contains("account_no=\"4930\"", xml);
  Assert.Contains("account_no=\"4730\"", xml);
  Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xml, "bu_code=\"9\"").Count);
 }
	[Fact]
	public void Omits_zero_value_sap_lines_and_zero_tax_amount_attributes()
	{
	 var lines = new[]
	 {
	  new DatevBookingLine("4125", "11", 5992.50m, 5992.50m, 0m, 0m, "EUR", "Innergemeinschaftliche Lieferung"),
	  new DatevBookingLine("4400", "3", 0m, 0m, 0m, 19m, "EUR", "Technische Nullzeile")
	 };
	 var xml = DatevInvoiceXmlGenerator.CreateOutgoing(new(910809,"910809",new DateOnly(2026,7,13),"Example Supplier Polska","PL123","","","","PL","Ausgang",5992.50m,5992.50m,0m,0m,"EUR","4125","11",BookingLines:lines));
	 var document = new System.Xml.XmlDocument(); document.LoadXml(xml);
	 Assert.Single(document.SelectNodes("/*[local-name()='invoice']/*[local-name()='invoice_item_list']")!.Cast<System.Xml.XmlNode>());
	 Assert.DoesNotContain("account_no=\"4400\"", xml);
	 Assert.DoesNotContain("tax_amount=\"0.00\"", xml);
	 Assert.Contains("tax=\"0.00\"", xml);
	}

	[Fact]
	public void Writes_the_datev_credit_note_type_explicitly()
	{
	 var xml = DatevInvoiceXmlGenerator.CreateIncoming(new(80003,"G-EXAMPLE-003",new DateOnly(2026,7,10),"Testlieferant GmbH","DE000000001","Straße 1","12345","Ort","DE","Eingangsgutschrift",1462.90m,1740.85m,277.95m,19m,"EUR","3400","9",BusinessPartnerAccountNumber:"70001",InvoiceType:"Gutschrift/Rechnungskorrektur"));
	 Assert.Contains("invoice_type=\"Gutschrift/Rechnungskorrektur\"", xml);
	}
}
