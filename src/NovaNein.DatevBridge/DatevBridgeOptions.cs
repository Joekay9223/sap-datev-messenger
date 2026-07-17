using NovaNein.Datev;
using System.Text.Json;

namespace NovaNein.DatevBridge;

public sealed record DatevBridgeOptions(
    string Root,
    string ShareRoot,
    string CredentialTarget,
    string IncomingFolder,
    string OutgoingFolder,
    string ShareStagingFolder,
    string[] XsdPaths)
{
    public static DatevBridgeOptions Load(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException("Die Bridge-Konfiguration wurde nicht gefunden.", path);
        var options = JsonSerializer.Deserialize<DatevBridgeOptions>(File.ReadAllText(path), DatevBridgeJson.SerializerOptions)
            ?? throw new InvalidDataException("Die Bridge-Konfiguration ist leer.");
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!Path.IsPathFullyQualified(Root)) throw new InvalidDataException("Root muss ein absoluter lokaler Pfad sein.");
        if (!OperatingSystem.IsWindows()) return;
        if (!ShareRoot.StartsWith(@"\\", StringComparison.Ordinal) || ShareRoot.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            throw new InvalidDataException("ShareRoot muss ein UNC-Freigabepfad sein.");
        if (string.IsNullOrWhiteSpace(CredentialTarget)) throw new InvalidDataException("CredentialTarget fehlt.");
        ValidateSingleFolder(IncomingFolder, nameof(IncomingFolder));
        ValidateSingleFolder(OutgoingFolder, nameof(OutgoingFolder));
        ValidateSingleFolder(ShareStagingFolder, nameof(ShareStagingFolder));
        if (!ShareStagingFolder.StartsWith(".", StringComparison.Ordinal))
            throw new InvalidDataException("Der nicht überwachte Stagingordner muss mit einem Punkt beginnen.");
        if (XsdPaths.Length == 0 || XsdPaths.Any(path => !Path.IsPathFullyQualified(path) || !File.Exists(path)))
            throw new InvalidDataException("Alle lokalen DATEV-XSDs müssen absolut konfiguriert und vorhanden sein.");
    }

    private static void ValidateSingleFolder(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != Path.GetFileName(value) || value.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidDataException($"{name} muss ein einzelner sicherer Ordnername sein.");
    }
}
