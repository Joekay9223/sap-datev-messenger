using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using NovaNein.Domain;

namespace NovaNein.Datev;

public sealed record PackageValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public static class DatevPackageRules
{
    public static void ValidateDocumentManifest(string xml, DocumentDirection direction, int docNum)
    {
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(xml);
        var root = document.DocumentElement;
        if (root is null || root.LocalName != "archive" || root.NamespaceURI != DatevDocumentXmlGenerator.Namespace || root.GetAttribute("version") != "6.0")
            throw new InvalidDataException("document.xml besitzt nicht den erwarteten DATEV-Archivvertrag Version 6.0.");
        var prefix = direction == DocumentDirection.Incoming ? "Eingangsrechnung" : "Ausgangsrechnung";
        var invoiceType = direction == DocumentDirection.Incoming ? "Incoming" : "Outgoing";
        var manager = new XmlNamespaceManager(document.NameTable); manager.AddNamespace("d", DatevDocumentXmlGenerator.Namespace); manager.AddNamespace("xsi", "http://www.w3.org/2001/XMLSchema-instance");
        var invoice = document.SelectSingleNode("/d:archive/d:content/d:document/d:extension[@xsi:type='Invoice']", manager) as XmlElement;
        var file = document.SelectSingleNode("/d:archive/d:content/d:document/d:extension[@xsi:type='File']", manager) as XmlElement;
        var property = invoice?.SelectSingleNode("d:property[@key='InvoiceType']", manager) as XmlElement;
        if (invoice?.GetAttribute("datafile") != $"{prefix}-{docNum}.xml" || file?.GetAttribute("name") != $"{prefix}-{docNum}.pdf" || property?.GetAttribute("value") != invoiceType)
            throw new InvalidDataException("document.xml verweist nicht exakt auf die DATEV-Rechnungs- und PDF-Datei.");
    }

    public static PackageValidationResult ValidateEntries(IEnumerable<string>? entryNames, DocumentDirection direction, int docNum)
    {
        var errors = new List<string>();
        if (entryNames is null) return new(false, ["Die ZIP-Einträge müssen angegeben werden."]);
        var names = entryNames.Select(x => x.Replace('\\', '/')).ToArray();
        var prefix = direction == DocumentDirection.Incoming ? "Eingangsrechnung" : "Ausgangsrechnung";
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "document.xml", $"{prefix}-{docNum}.xml", $"{prefix}-{docNum}.pdf"
        };

        if (names.Length != 3) errors.Add("Ein DATEV-ZIP muss exakt drei Dateien enthalten.");
        if (names.Any(x => x.Contains('/') || x.StartsWith(".", StringComparison.Ordinal))) errors.Add("ZIP-Dateien müssen im Stammverzeichnis liegen.");
        if (!expected.SetEquals(names)) errors.Add("Die DATEV-Dateinamen stimmen nicht exakt mit Belegart und SAP-Dokumentnummer überein.");
        if (names.GroupBy(x => x, StringComparer.Ordinal).Any(x => x.Count() != 1)) errors.Add("Jede DATEV-Datei darf nur einmal enthalten sein.");
        return new(errors.Count == 0, errors);
    }

    public static string Sha256(Stream content)
    {
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(content));
    }
}

public sealed class AtomicWatchfolderTransfer
{
    public void MoveCompletedPackage(string sourceZip, string watchfolder, string targetFileName, bool deleteSource = true)
    {
        if (!Path.IsPathFullyQualified(sourceZip) || !Path.IsPathFullyQualified(watchfolder))
            throw new InvalidOperationException("Für die Übergabe sind absolute Pfade erforderlich.");
        if (!File.Exists(sourceZip)) throw new FileNotFoundException("Das Paket-ZIP wurde nicht gefunden.", sourceZip);
        if (!string.Equals(Path.GetExtension(sourceZip), ".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Nur geprüfte ZIP-Pakete dürfen übergeben werden.");
        if (!string.Equals(targetFileName, Path.GetFileName(targetFileName), StringComparison.Ordinal) || !targetFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Der DATEV-Zielname muss ein reiner ZIP-Dateiname sein.");
        Directory.CreateDirectory(watchfolder);
        var target = Path.Combine(watchfolder, targetFileName);
        if (File.Exists(target))
        {
            if (!FilesMatch(sourceZip, target)) throw new IOException("Zielpaket existiert bereits mit abweichendem Inhalt; eine Dublette wird nicht überschrieben.");
            if (deleteSource) File.Delete(sourceZip);
            return;
        }

        // Das Paket wird zuerst vollständig in eine zufällige Datei auf dem
        // Zielvolume kopiert. Nur die abschließende Umbenennung im Watchfolder
        // ist sichtbar und damit auch bei einer UNC-Freigabe atomar.
        var temporary = Path.Combine(watchfolder, $".{targetFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = new FileStream(sourceZip, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            if (!FilesMatch(sourceZip, temporary)) throw new IOException("Die DATEV-Zielkopie stimmt nicht mit dem geprüften Quellpaket überein.");
            try { File.Move(temporary, target); }
            catch (IOException) when (File.Exists(target) && FilesMatch(sourceZip, target)) { }
            if (deleteSource) File.Delete(sourceZip);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private static bool FilesMatch(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return string.Equals(DatevPackageRules.Sha256(leftStream), DatevPackageRules.Sha256(rightStream), StringComparison.Ordinal);
    }
}
