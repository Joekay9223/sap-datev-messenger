using System.Security.Cryptography;
using System.Text.Json;
using NovaNein.Datev;
using NovaNein.Domain;

namespace NovaNein.DatevBridge;

public sealed class DatevBridgeRunner(DatevBridgeOptions options)
{
    private const int MaximumAttempts = 5;

    public int RunOnce()
    {
        var paths = BridgeLocalPaths.From(options.Root);
        paths.EnsureCreated();
        try
        {
            var credential = WindowsCredentialStore.Read(options.CredentialTarget);
            NetworkShareConnection.EnsureConnected(options.ShareRoot, credential);
            Directory.CreateDirectory(Path.Combine(options.ShareRoot, options.ShareStagingFolder));
            var processed = 0;
            foreach (var manifestPath in Directory.EnumerateFiles(paths.Manifests, "*.json", SearchOption.TopDirectoryOnly).OrderBy(File.GetCreationTimeUtc))
            {
                ProcessManifest(manifestPath, paths);
                processed++;
            }
            WriteHeartbeat(paths.Heartbeat, "ready", null);
            return processed;
        }
        catch (Exception ex)
        {
            WriteHeartbeat(paths.Heartbeat, "error", SafeError(ex));
            throw;
        }
    }

    private void ProcessManifest(string manifestPath, BridgeLocalPaths paths)
    {
        DatevBridgeManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<DatevBridgeManifest>(File.ReadAllText(manifestPath), DatevBridgeJson.SerializerOptions)
                ?? throw new InvalidDataException("Das Bridge-Manifest ist leer.");
            ValidateManifest(manifest, manifestPath);
            var packagePath = Path.Combine(paths.Packages, manifest.RequestId.ToString("D"), manifest.PackageFileName);
            DatevPackageInspector.Inspect(packagePath, manifest.Direction, manifest.DocNum, manifest.PackageSha256, options.XsdPaths);

            var targetFolderName = manifest.Direction == DocumentDirection.Incoming ? options.IncomingFolder : options.OutgoingFolder;
            var shareStaging = Path.Combine(options.ShareRoot, options.ShareStagingFolder);
            var watchfolder = Path.Combine(options.ShareRoot, targetFolderName);
            Directory.CreateDirectory(shareStaging);
            if (!Directory.Exists(watchfolder)) throw new DirectoryNotFoundException("Der konfigurierte DATEV-Watchfolder ist nicht erreichbar.");
            EnsureZipOnlyWatchfolderName(manifest.PackageFileName);

            var shareTemporary = Path.Combine(shareStaging, $"{manifest.RequestId:D}-{manifest.PackageFileName}.partial");
            var shareReady = Path.Combine(shareStaging, $"{manifest.RequestId:D}-{manifest.PackageFileName}");
            var target = Path.Combine(watchfolder, manifest.PackageFileName);
            if (File.Exists(target))
            {
                if (!HashMatches(target, manifest.PackageSha256))
                    throw new IOException("Im DATEV-Watchfolder existiert ein gleichnamiges ZIP mit abweichender Prüfsumme; es wurde nichts überschrieben.");
            }
            else
            {
                File.Delete(shareTemporary);
                File.Copy(packagePath, shareTemporary, overwrite: false);
                if (!HashMatches(shareTemporary, manifest.PackageSha256))
                    throw new IOException("Die DATEV-Stagingkopie besitzt eine abweichende Prüfsumme.");
                if (File.Exists(shareReady))
                {
                    if (!HashMatches(shareReady, manifest.PackageSha256))
                        throw new IOException("Im DATEV-Staging existiert ein gleichnamiges ZIP mit abweichender Prüfsumme.");
                    File.Delete(shareTemporary);
                }
                else File.Move(shareTemporary, shareReady);
                File.Move(shareReady, target, overwrite: false);
            }

            if (!HashMatches(target, manifest.PackageSha256))
                throw new IOException("Die ZIP-Prüfsumme im DATEV-Watchfolder ist nach der atomaren Übergabe abweichend.");
            var attempts = ReadAttempts(paths.State, manifest.RequestId) + 1;
            WriteReceipt(paths.Receipts, new(1, manifest.RequestId, manifest.DocumentId, manifest.PackageFileName,
                manifest.PackageSha256, true, attempts, DateTimeOffset.UtcNow, null));
            ArchiveLocal(manifestPath, packagePath, paths.Archive, manifest.RequestId);
            DeleteState(paths.State, manifest.RequestId);
        }
        catch (Exception ex) when (manifest is not null)
        {
            var attempts = IncrementAttempts(paths.State, manifest.RequestId, SafeError(ex));
            if (attempts >= MaximumAttempts)
            {
                WriteReceipt(paths.Receipts, new(1, manifest.RequestId, manifest.DocumentId, manifest.PackageFileName,
                    manifest.PackageSha256, false, attempts, DateTimeOffset.UtcNow, SafeError(ex)));
                ArchiveError(manifestPath, paths.Errors, manifest.RequestId);
            }
            throw;
        }
    }

    private static void ValidateManifest(DatevBridgeManifest manifest, string manifestPath)
    {
        if (manifest.Version != 1 || manifest.RequestId == Guid.Empty || manifest.DocumentId == Guid.Empty || manifest.DocNum <= 0)
            throw new InvalidDataException("Das Bridge-Manifest besitzt eine ungültige Version oder Identität.");
        if (manifest.Direction is not (DocumentDirection.Incoming or DocumentDirection.Outgoing))
            throw new InvalidDataException("Das Bridge-Manifest besitzt eine ungültige Belegart.");
        if (!string.Equals(Path.GetFileNameWithoutExtension(manifestPath), manifest.RequestId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifestdateiname und Transferauftrags-ID stimmen nicht überein.");
        EnsureZipOnlyWatchfolderName(manifest.PackageFileName);
    }

    private static void EnsureZipOnlyWatchfolderName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die Bridge darf ausschließlich sichere geschlossene ZIP-Dateien übergeben.");
    }

    private static bool HashMatches(string path, string expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteReceipt(string root, DatevBridgeReceipt receipt) =>
        AtomicWrite(Path.Combine(root, receipt.RequestId.ToString("D") + ".json"), JsonSerializer.Serialize(receipt, DatevBridgeJson.SerializerOptions), allowIdentical: true);

    private static void WriteHeartbeat(string path, string status, string? error) =>
        AtomicWrite(path, JsonSerializer.Serialize(new DatevBridgeHeartbeat(1, DateTimeOffset.UtcNow, status, error), DatevBridgeJson.SerializerOptions), allowIdentical: false);

    private static int ReadAttempts(string root, Guid requestId)
    {
        var path = Path.Combine(root, requestId.ToString("D") + ".json");
        if (!File.Exists(path)) return 0;
        return JsonSerializer.Deserialize<BridgeAttemptState>(File.ReadAllText(path), DatevBridgeJson.SerializerOptions)?.Attempts ?? 0;
    }

    private static int IncrementAttempts(string root, Guid requestId, string error)
    {
        var attempts = ReadAttempts(root, requestId) + 1;
        AtomicWrite(Path.Combine(root, requestId.ToString("D") + ".json"),
            JsonSerializer.Serialize(new BridgeAttemptState(attempts, DateTimeOffset.UtcNow, error), DatevBridgeJson.SerializerOptions), allowIdentical: false);
        return attempts;
    }

    private static void DeleteState(string root, Guid requestId)
    {
        var path = Path.Combine(root, requestId.ToString("D") + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static void ArchiveLocal(string manifestPath, string packagePath, string archiveRoot, Guid requestId)
    {
        var destination = Path.Combine(archiveRoot, requestId.ToString("D"));
        Directory.CreateDirectory(destination);
        MoveIdempotent(packagePath, Path.Combine(destination, Path.GetFileName(packagePath)));
        MoveIdempotent(manifestPath, Path.Combine(destination, Path.GetFileName(manifestPath)));
        var packageDirectory = Path.GetDirectoryName(packagePath)!;
        if (Directory.Exists(packageDirectory) && !Directory.EnumerateFileSystemEntries(packageDirectory).Any()) Directory.Delete(packageDirectory);
    }

    private static void ArchiveError(string manifestPath, string errorsRoot, Guid requestId)
    {
        Directory.CreateDirectory(errorsRoot);
        MoveIdempotent(manifestPath, Path.Combine(errorsRoot, requestId.ToString("D") + ".json"));
    }

    private static void MoveIdempotent(string source, string target)
    {
        if (!File.Exists(source)) return;
        if (File.Exists(target)) File.Delete(source); else File.Move(source, target);
    }

    private static void AtomicWrite(string path, string content, bool allowIdentical)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && allowIdentical)
        {
            if (string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal)) return;
            throw new IOException("Eine abweichende Quittung existiert bereits.");
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string SafeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 1000 ? message : message[..1000];
    }

    private sealed record BridgeAttemptState(int Attempts, DateTimeOffset LastAttemptAt, string LastError);
    private sealed record BridgeLocalPaths(string Root, string Packages, string Manifests, string Receipts, string Archive, string Errors, string State, string Heartbeat)
    {
        public static BridgeLocalPaths From(string root)
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return new(root, Path.Combine(root, "outbox", "packages"), Path.Combine(root, "outbox", "manifests"),
                Path.Combine(root, "receipts"), Path.Combine(root, "archive"), Path.Combine(root, "errors"),
                Path.Combine(root, "state"), Path.Combine(root, "heartbeat.json"));
        }

        public void EnsureCreated()
        {
            foreach (var path in new[] { Root, Packages, Manifests, Receipts, Archive, Errors, State }) Directory.CreateDirectory(path);
        }
    }
}
