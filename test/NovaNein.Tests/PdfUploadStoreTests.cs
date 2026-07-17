using System.Security.Cryptography;
using System.Text;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class PdfUploadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"novanein-pdf-upload-{Guid.NewGuid():N}");

    [Fact]
    public async Task StoreAsync_RejectsInvalidPdfSignature()
    {
        var store = new PdfUploadStore(_root, maximumBytes: 100);
        await using var content = new MemoryStream(Encoding.ASCII.GetBytes("not-a-pdf"));

        await Assert.ThrowsAsync<InvalidPdfUploadException>(() => store.StoreAsync("invoice.pdf", content.Length, content));

        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task StoreAsync_RejectsActualStreamOverLimitEvenWhenDeclaredLengthIsSmaller()
    {
        var store = new PdfUploadStore(_root, maximumBytes: 16);
        await using var content = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-123456789012"));

        var exception = await Assert.ThrowsAsync<PdfUploadTooLargeException>(
            () => store.StoreAsync("invoice.pdf", declaredLength: 5, content));

        Assert.Equal(16, exception.MaximumBytes);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task StoreAsync_AtomicallyStoresBySha256AndDeduplicates()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\nsmall test document");
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
        var store = new PdfUploadStore(_root, maximumBytes: 100);

        await using var firstContent = new MemoryStream(bytes);
        var first = await store.StoreAsync("invoice.pdf", bytes.Length, firstContent);
        await using var duplicateContent = new MemoryStream(bytes);
        var duplicate = await store.StoreAsync("renamed.PDF", bytes.Length, duplicateContent);

        Assert.True(first.StoredNew);
        Assert.False(duplicate.StoredNew);
        Assert.Equal(expectedHash, first.Sha256);
        Assert.Equal(first.Path, duplicate.Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.Path));
        Assert.Equal(new[] { $"{expectedHash}.pdf" }, Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task StoreAsync_RemovesRandomTempFileWhenSourceReadFails()
    {
        var store = new PdfUploadStore(_root, maximumBytes: 100);
        await using var content = new FailingReadStream(Encoding.ASCII.GetBytes("%PDF-valid-prefix"));

        await Assert.ThrowsAsync<IOException>(() => store.StoreAsync("invoice.pdf", declaredLength: 20, content));

        Assert.Empty(Directory.GetFiles(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FailingReadStream(byte[] firstChunk) : Stream
    {
        private bool _returnedFirstChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_returnedFirstChunk) return ValueTask.FromException<int>(new IOException("Simulierter Lesefehler."));
            _returnedFirstChunk = true;
            firstChunk.CopyTo(buffer);
            return ValueTask.FromResult(firstChunk.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Cleanup_removes_only_old_unreferenced_hash_named_pdfs()
    {
        var store = new PdfUploadStore(_root, 1024);
        Directory.CreateDirectory(_root);
        var referencedHash = new string('A', 64);
        var orphanHash = new string('B', 64);
        var recentHash = new string('C', 64);
        var referenced = Path.Combine(_root, referencedHash + ".pdf");
        var orphan = Path.Combine(_root, orphanHash + ".pdf");
        var recent = Path.Combine(_root, recentHash + ".pdf");
        var unrelated = Path.Combine(_root, "manuell.pdf");
        await File.WriteAllTextAsync(referenced, "%PDF-ref");
        await File.WriteAllTextAsync(orphan, "%PDF-orphan");
        await File.WriteAllTextAsync(recent, "%PDF-recent");
        await File.WriteAllTextAsync(unrelated, "%PDF-manual");
        File.SetLastWriteTimeUtc(referenced, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));

        var deleted = await store.CleanupOrphansAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { referencedHash }, TimeSpan.FromHours(24));

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
    }
}
