using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovaNein.Datev;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class DatevTransferAgent(
    DatevTransferRequestStore requests,
    TransferEvidenceStore evidence,
    IConfiguration configuration,
    ILogger<DatevTransferAgent> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Datev:TransferAgentEnabled", false))
        {
            logger.LogInformation("Der DATEV-Transfer-Agent ist deaktiviert; bestätigte Aufträge bleiben im Cockpit sichtbar.");
            return;
        }
        if (!string.Equals(configuration["Datev:TransferMode"], "LocalBridge", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Für den sicheren Transfer ist Datev:TransferMode=LocalBridge erforderlich.");
            return;
        }

        var paths = BridgePaths.From(configuration);
        paths.EnsureCreated();
        var recovered = await requests.RecoverInProgressAsync(stoppingToken);
        if (recovered > 0) logger.LogWarning("{Count} unterbrochene DATEV-Stagingaufträge wurden sicher als fehlgeschlagen markiert.", recovered);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessReceiptsAsync(paths, stoppingToken);
                var request = await requests.ClaimNextAsync(stoppingToken);
                if (request is not null) await StageAsync(request, paths, stoppingToken);
                else await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Der lokale DATEV-Bridge-Agent konnte seinen Durchlauf nicht abschließen.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task StageAsync(DatevTransferRequest request, BridgePaths paths, CancellationToken cancellationToken)
    {
        try
        {
            var package = await evidence.GetAsync(request.DocumentId, cancellationToken)
                ?? throw new InvalidOperationException("Für den Transferauftrag fehlt der DATEV-Paketnachweis.");
            if (!string.Equals(package.PackageSha256, request.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die Transfer-Prüfsumme stimmt nicht mit dem vorbereiteten DATEV-Paket überein.");

            var identity = await ResolveDocumentAsync(request.DocumentId, cancellationToken);
            var source = FindPackage(paths.PackageRoot, package.PackageFileName, package.PackageSha256);
            var xsdPaths = configuration.GetSection("Datev:XsdPaths").Get<string[]>() ?? [];
            DatevPackageInspector.Inspect(source, identity.Direction, identity.DocNum, package.PackageSha256, xsdPaths);

            var requestDirectory = Path.Combine(paths.OutboxPackages, request.Id.ToString("D"));
            Directory.CreateDirectory(requestDirectory);
            var stagedPackage = Path.Combine(requestDirectory, package.PackageFileName);
            CopyVerified(source, stagedPackage, package.PackageSha256);
            DatevPackageInspector.Inspect(stagedPackage, identity.Direction, identity.DocNum, package.PackageSha256, xsdPaths);

            var manifest = new DatevBridgeManifest(1, request.Id, request.DocumentId, identity.Direction, identity.DocNum,
                package.PackageFileName, package.PackageSha256, DateTimeOffset.UtcNow);
            AtomicWriteManifest(Path.Combine(paths.OutboxManifests, request.Id.ToString("D") + ".json"), manifest);
            if (await requests.MarkBridgeStagedAsync(request.Id, manifest.CreatedAt, cancellationToken) is null)
                throw new InvalidOperationException("Der DATEV-Auftrag konnte nicht als lokal bereitgestellt markiert werden.");
            logger.LogInformation("DATEV-Paket {Package} wurde mit Hash {Hash} im lokalen Bridge-Ausgang bereitgestellt.", package.PackageFileName, package.PackageSha256);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "DATEV-Transferauftrag {RequestId} konnte nicht lokal bereitgestellt werden.", request.Id);
            await requests.MarkFailedAsync(request.Id, SafeError(ex), cancellationToken);
        }
    }

    private async Task ProcessReceiptsAsync(BridgePaths paths, CancellationToken cancellationToken)
    {
        foreach (var receiptPath in Directory.EnumerateFiles(paths.Receipts, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var receipt = JsonSerializer.Deserialize<DatevBridgeReceipt>(await File.ReadAllTextAsync(receiptPath, cancellationToken), JsonOptions)
                    ?? throw new InvalidDataException("Die Bridge-Quittung ist leer.");
                if (receipt.Version != 1 || receipt.RequestId == Guid.Empty || receipt.DocumentId == Guid.Empty)
                    throw new InvalidDataException("Die Bridge-Quittung besitzt eine ungültige Version oder ID.");
                if (!string.Equals(receipt.PackageFileName, Path.GetFileName(receipt.PackageFileName), StringComparison.Ordinal)
                    || !receipt.PackageFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Die Bridge-Quittung enthält keinen sicheren ZIP-Dateinamen.");

                var request = await requests.GetByIdAsync(receipt.RequestId, cancellationToken)
                    ?? throw new KeyNotFoundException("Der Transferauftrag zur Bridge-Quittung fehlt.");
                if (request.DocumentId != receipt.DocumentId
                    || !string.Equals(request.PackageSha256, receipt.PackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Bridge-Quittung und Transferauftrag stimmen nicht überein.");
				await requests.UpdateBridgeAttemptsAsync(request.Id, receipt.Attempts, cancellationToken);

                if (receipt.Succeeded)
                {
                    if (await requests.MarkWatchfolderDeliveredAsync(request.Id, receipt.OccurredAt, cancellationToken) is null
                        && request.Status != "watchfolder-delivered" && request.Status != "awaiting-datev-confirmation" && request.Status != "finalized")
                        throw new InvalidOperationException("Die bestätigte DATEV-Bridge-Übergabe konnte nicht gespeichert werden.");
                    await evidence.ReconcileDocumentAsync(request.DocumentId, cancellationToken);
                    var current = await requests.GetByIdAsync(request.Id, cancellationToken);
                    if (current?.Status == "watchfolder-delivered")
						await requests.MarkAwaitingDatevConfirmationAsync(request.Id, cancellationToken);
                }
                else if (receipt.Attempts >= 5)
                {
                    await requests.MarkFailedAsync(request.Id, SafeError(new IOException(receipt.Error ?? "DATEV-Bridge fehlgeschlagen.")), cancellationToken);
                }
                else
                {
                    throw new InvalidDataException("Eine Fehlerquittung darf erst nach fünf Bridge-Versuchen übernommen werden.");
                }

                MoveReceipt(receiptPath, paths.ReceiptArchive);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Die Bridge-Quittung {Receipt} wurde in die Fehlerablage verschoben.", Path.GetFileName(receiptPath));
                MoveReceipt(receiptPath, paths.Errors);
            }
        }
    }

    private async Task<(DocumentDirection Direction, int DocNum)> ResolveDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var databasePath = configuration["Storage:DatabasePath"] ?? "data/novanein.db";
        await using var connection = new SqliteConnection("Data Source=" + databasePath); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT direction,doc_num FROM documents WHERE id=$id"; command.Parameters.AddWithValue("$id", documentId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Der Beleg zum Transferauftrag fehlt.");
        var direction = (DocumentDirection)reader.GetInt32(0); var docNum = reader.GetInt32(1);
        if (direction is not (DocumentDirection.Incoming or DocumentDirection.Outgoing) || docNum <= 0)
            throw new InvalidDataException("Der Beleg zum Transferauftrag besitzt eine ungültige DATEV-Identität.");
        return (direction, docNum);
    }

    private static string FindPackage(string packageRoot, string packageFileName, string expectedHash)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        var prefix = root + Path.DirectorySeparatorChar;
        foreach (var candidate in Directory.EnumerateFiles(root, packageFileName, SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(candidate);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = File.OpenRead(full);
            if (string.Equals(DatevPackageRules.Sha256(stream), expectedHash, StringComparison.OrdinalIgnoreCase)) return full;
        }
        throw new FileNotFoundException("Das vorbereitete DATEV-Paket mit der erwarteten Prüfsumme wurde nicht gefunden.", packageFileName);
    }

    private static void CopyVerified(string source, string target, string expectedHash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            using var existing = File.OpenRead(target);
            if (string.Equals(DatevPackageRules.Sha256(existing), expectedHash, StringComparison.OrdinalIgnoreCase)) return;
            throw new IOException("Im lokalen Bridge-Ausgang existiert ein gleichnamiges Paket mit abweichender Prüfsumme.");
        }
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            using (var check = File.OpenRead(temporary))
                if (!string.Equals(DatevPackageRules.Sha256(check), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Die lokale Bridge-Kopie besitzt eine abweichende Prüfsumme.");
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void AtomicWriteManifest(string path, DatevBridgeManifest value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<DatevBridgeManifest>(File.ReadAllText(path), JsonOptions);
            if (existing is not null && existing.Version == value.Version && existing.RequestId == value.RequestId
                && existing.DocumentId == value.DocumentId && existing.Direction == value.Direction && existing.DocNum == value.DocNum
                && string.Equals(existing.PackageFileName, value.PackageFileName, StringComparison.Ordinal)
                && string.Equals(existing.PackageSha256, value.PackageSha256, StringComparison.OrdinalIgnoreCase)) return;
            throw new IOException("Im lokalen Bridge-Ausgang existiert ein abweichendes Manifest für denselben Auftrag.");
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void MoveReceipt(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var target = Path.Combine(destination, Path.GetFileName(source));
        if (File.Exists(target)) target = Path.Combine(destination, Path.GetFileNameWithoutExtension(source) + "-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(source, target);
    }

    private static string SafeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 1000 ? message : message[..1000];
    }

    private sealed record BridgePaths(string Root, string PackageRoot, string OutboxPackages, string OutboxManifests, string Receipts, string ReceiptArchive, string Errors)
    {
        public static BridgePaths From(IConfiguration configuration)
        {
            var root = configuration["Datev:Bridge:Root"] ?? throw new InvalidOperationException("Datev:Bridge:Root fehlt.");
            var packages = configuration["Datev:PackageDirectory"] ?? throw new InvalidOperationException("Datev:PackageDirectory fehlt.");
            if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(packages)) throw new InvalidOperationException("DATEV-Bridge- und Paketverzeichnis müssen absolut sein.");
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            packages = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packages));
            if (root.StartsWith(packages + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || packages.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(root, packages, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("DATEV-Bridge und kanonischer Paketbereich müssen getrennt sein.");
            return new(root, packages, Path.Combine(root, "outbox", "packages"), Path.Combine(root, "outbox", "manifests"), Path.Combine(root, "receipts"), Path.Combine(root, "receipt-archive"), Path.Combine(root, "errors"));
        }

        public void EnsureCreated()
        {
            foreach (var path in new[] { Root, OutboxPackages, OutboxManifests, Receipts, ReceiptArchive, Errors }) Directory.CreateDirectory(path);
        }
    }
}
