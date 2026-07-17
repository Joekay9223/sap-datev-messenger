using System.Collections;
using System.Reflection;
using System.Text;

namespace NovaNein.SapAddon;

public sealed record CoresuiteExportRequest(
    string RuntimeDirectory,
    IReadOnlyDictionary<string, string> ConnectionValues,
    string DocumentKey,
    string PrintDefinitionId);

public sealed record CoresuiteExportResult(string PdfPath, IReadOnlyDictionary<string, string> Metadata);

/// <summary>Runs the installed Coresuite print definition without rebuilding its layout.</summary>
public sealed class CoresuitePdfExporter
{
    private const string ServiceApiAssembly = "CoresuiteServiceAPI.dll";

    public CoresuiteExportResult Export(CoresuiteExportRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (!Directory.Exists(request.RuntimeDirectory)) throw new DirectoryNotFoundException("Das Coresuite-Laufzeitverzeichnis wurde nicht gefunden.");
        if (string.IsNullOrWhiteSpace(request.DocumentKey)) throw new ArgumentException("Der SAP-Dokumentschlüssel ist erforderlich.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PrintDefinitionId)) throw new ArgumentException("Die Coresuite-Print-Definition ist erforderlich.", nameof(request));

        var assemblyPath = Path.Combine(request.RuntimeDirectory, ServiceApiAssembly);
        if (!File.Exists(assemblyPath)) throw new FileNotFoundException("CoresuiteServiceAPI.dll wurde nicht gefunden.", assemblyPath);
        var assembly = Assembly.LoadFrom(assemblyPath);
        var configType = assembly.GetType("CoresuiteServiceAPI.Config", throwOnError: true)!;
        var printApiType = assembly.GetType("CoresuiteServiceAPI.Designer.PrintAPI", throwOnError: true)!;
        var config = Activator.CreateInstance(configType) ?? throw new InvalidOperationException("Coresuite-Konfiguration konnte nicht erzeugt werden.");

        foreach (var value in request.ConnectionValues)
        {
            var property = configType.GetProperty(value.Key, BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanWrite) throw new ArgumentException($"Unbekannte Coresuite-Konfigurationseigenschaft: {value.Key}", nameof(request));
            property.SetValue(config, value.Value);
        }

        var printApi = Activator.CreateInstance(printApiType, config) ?? throw new InvalidOperationException("Coresuite PrintAPI konnte nicht gestartet werden.");
        var generatePdf = printApiType.GetMethod("GeneratePdf", [typeof(string), typeof(string)])
            ?? throw new MissingMethodException("Coresuite PrintAPI.GeneratePdf(string, string) wurde nicht gefunden.");
        // Coresuite's API signature is GeneratePdf(printDefinitionNameId, docEntry).
        // Keep the request model document-first, but pass the arguments in the
        // order expected by the installed Coresuite runtime.
        var printResult = generatePdf.Invoke(printApi, [request.PrintDefinitionId, request.DocumentKey])
            ?? throw new InvalidOperationException("Coresuite lieferte kein Druckergebnis.");
        var resultType = printResult.GetType();
        var success = (bool?)resultType.GetProperty("Success")?.GetValue(printResult) ?? false;
        var error = resultType.GetProperty("ErrorMessage")?.GetValue(printResult)?.ToString();
        if (!success) throw new InvalidOperationException($"Coresuite-PDF-Export fehlgeschlagen: {error ?? "ohne Fehlermeldung"}");

        var paths = ((IEnumerable?)resultType.GetProperty("ExportPath")?.GetValue(printResult))
            ?.Cast<object>().Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
            .Where(x => x.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(x)).ToArray() ?? [];
        if (paths.Length != 1) throw new InvalidOperationException("Coresuite muss genau eine existierende PDF liefern.");
        using (var stream = File.OpenRead(paths[0]))
        {
            var signature = new byte[5];
            if (stream.Read(signature, 0, signature.Length) != signature.Length || Encoding.ASCII.GetString(signature) != "%PDF-") throw new InvalidDataException("Coresuite lieferte keine gültige PDF-Datei.");
        }
        var metadata = ((IDictionary?)resultType.GetProperty("Metadata")?.GetValue(printResult))
            ?.Cast<DictionaryEntry>().ToDictionary(x => x.Key?.ToString() ?? string.Empty, x => x.Value?.ToString() ?? string.Empty, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return new CoresuiteExportResult(paths[0], metadata);
    }
}
