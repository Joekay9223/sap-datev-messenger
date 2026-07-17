using System.Buffers;
using System.Security.Cryptography;

namespace NovaNein.Server;

public sealed record PdfUploadStoreResult(string Sha256, string Path, bool StoredNew);

public sealed class InvalidPdfUploadException(string message) : Exception(message);

public sealed class PdfUploadTooLargeException(long maximumBytes)
    : Exception($"Die PDF überschreitet die maximal zulässige Größe von {maximumBytes} Bytes.")
{
    public long MaximumBytes { get; } = maximumBytes;
}

public sealed class PdfUploadStore
{
    public const long MaximumPdfBytes = 20L * 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private static ReadOnlySpan<byte> PdfSignature => "%PDF-"u8;

    private readonly string _documentRoot;
    private readonly long _maximumBytes;

    public PdfUploadStore(IConfiguration configuration)
        : this(configuration["Storage:DocumentRoot"] ?? "data/documents")
    {
    }

    public PdfUploadStore(string documentRoot, long maximumBytes = MaximumPdfBytes)
    {
        if (string.IsNullOrWhiteSpace(documentRoot)) throw new ArgumentException("Ein Dokumentverzeichnis ist erforderlich.", nameof(documentRoot));
        if (maximumBytes < PdfSignature.Length) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _documentRoot = Path.GetFullPath(documentRoot);
        _maximumBytes = maximumBytes;
    }

    public async Task<PdfUploadStoreResult> StoreAsync(
        string originalFileName,
        long declaredLength,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!string.Equals(Path.GetExtension(originalFileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidPdfUploadException("Die Datei muss die Endung .pdf besitzen.");
        if (declaredLength > _maximumBytes)
            throw new PdfUploadTooLargeException(_maximumBytes);

        Directory.CreateDirectory(_documentRoot);
        var stagingPath = Path.Combine(_documentRoot, $".upload-{RandomNumberGenerator.GetHexString(16)}.tmp");
        try
        {
            string hash;
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var signature = new byte[PdfSignature.Length];
                var signatureLength = 0;
                var signatureValidated = false;
                long actualLength = 0;

                await using (var target = new FileStream(
                    stagingPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        var bytesRead = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                        if (bytesRead == 0) break;

                        if (actualLength > _maximumBytes - bytesRead)
                            throw new PdfUploadTooLargeException(_maximumBytes);
                        actualLength += bytesRead;

                        if (!signatureValidated)
                        {
                            var bytesForSignature = Math.Min(bytesRead, signature.Length - signatureLength);
                            buffer.AsSpan(0, bytesForSignature).CopyTo(signature.AsSpan(signatureLength));
                            signatureLength += bytesForSignature;
                            if (signatureLength == signature.Length)
                            {
                                if (!signature.AsSpan().SequenceEqual(PdfSignature))
                                    throw new InvalidPdfUploadException("Die Datei besitzt keine gültige PDF-Signatur.");
                                signatureValidated = true;
                            }
                        }

                        sha256.AppendData(buffer, 0, bytesRead);
                        await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    }

                    if (!signatureValidated)
                        throw new InvalidPdfUploadException("Die Datei besitzt keine gültige PDF-Signatur.");

                    await target.FlushAsync(cancellationToken);
                    target.Flush(flushToDisk: true);
                }

                hash = Convert.ToHexString(sha256.GetHashAndReset());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var finalPath = Path.Combine(_documentRoot, $"{hash}.pdf");
            try
            {
                File.Move(stagingPath, finalPath);
                return new PdfUploadStoreResult(hash, finalPath, StoredNew: true);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Eine parallele, inhaltsgleiche Übernahme hat denselben SHA-256-Pfad bereits atomar angelegt.
                return new PdfUploadStoreResult(hash, finalPath, StoredNew: false);
            }
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    public Task<int> CleanupOrphansAsync(IReadOnlySet<string> referencedHashes, TimeSpan minimumAge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(referencedHashes);
        if (minimumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumAge));
        if (!Directory.Exists(_documentRoot)) return Task.FromResult(0);

        var threshold = DateTime.UtcNow - minimumAge;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_documentRoot, "*.pdf", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Path.GetFileNameWithoutExtension(path);
            if (!IsSha256(hash) || referencedHashes.Contains(hash)) continue;
            try
            {
                // Die Schonfrist verhindert ein Rennen mit einer gerade laufenden Aufnahme, deren
                // Datenbanktransaktion erst nach dem atomaren Dateiverschieben abgeschlossen wird.
                if (File.GetLastWriteTimeUtc(path) > threshold) continue;
                File.Delete(path);
                deleted++;
            }
            catch (FileNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Task.FromResult(deleted);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(character =>
        character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Der ursprüngliche Fehler ist aussagekräftiger; ein späterer Wartungslauf kann die Tempdatei entfernen.
        }
        catch (UnauthorizedAccessException)
        {
            // Siehe oben. Erfolgreich gespeicherte PDFs werden nie über diesen Pfad gelöscht.
        }
    }
}
