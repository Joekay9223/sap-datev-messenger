using Microsoft.Extensions.Configuration;
using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class SapAttachmentProcessorTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-attachment-{Guid.NewGuid():N}");
    private IConfiguration _configuration = null!;
    private DocumentStore _documents = null!;

    public async Task InitializeAsync()
    {
        var documents = Path.Combine(_directory, "documents");
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DatabasePath"] = Path.Combine(_directory, "archive.db"), ["Storage:DocumentRoot"] = documents,
            ["Sap:Mode"] = "write-enabled", ["Sap:EnableAttachments2Writes"] = "true", ["Sap:AutoAttachApprovedDocuments"] = "true", ["Sap:AttachmentSourceRoot"] = _directory
        }).Build();
        _documents = new DocumentStore(_configuration); await _documents.InitializeAsync();
    }

    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }

    [Fact]
    public async Task Attaches_only_an_approved_document_and_records_the_verified_state()
    {
        var hash = new string('D', 64);
        var item = await _documents.CreateAsync(new(DocumentDirection.Incoming, 90002, 900002), hash, "test.pdf", "tester");
        await _documents.RecordValidationAsync(item.Id, new(ReviewSignal.Green, []), "tester");
        var root = _configuration["Storage:DocumentRoot"]!; Directory.CreateDirectory(root); await File.WriteAllTextAsync(Path.Combine(root, hash + ".pdf"), "%PDF-test");
        var sap = new RecordingSap();
        var processor = new SapAttachmentProcessor(_documents, sap, _configuration);

        await processor.ProcessAsync(new(Guid.NewGuid(), item.Id, DocumentJobKind.AttachToSap, DocumentJobState.Running, 1, DateTimeOffset.UtcNow, null));

        Assert.NotNull(sap.AttachmentRequest);
        Assert.Equal(SapDocumentKind.PurchaseInvoice, sap.AttachmentRequest!.Value.Kind);
        Assert.Equal(90002, sap.AttachmentRequest!.Value.Entry);
        Assert.Equal(DocumentStatus.AttachedToSap, (await _documents.GetAsync(item.Id))!.Status);
        Assert.Contains(await _documents.EventsAsync(item.Id), e => e.Kind == "SapAttachmentVerified");
    }

    [Fact]
    public Task Does_not_enable_auto_attach_when_document_storage_is_outside_the_source_root()
    {
        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DocumentRoot"] = Path.GetTempPath(), ["Sap:Mode"] = "write-enabled", ["Sap:EnableAttachments2Writes"] = "true", ["Sap:AutoAttachApprovedDocuments"] = "true", ["Sap:AttachmentSourceRoot"] = Path.Combine(_directory, "only-this-folder")
        }).Build();
        Assert.False(new SapAttachmentProcessor(_documents, new RecordingSap(), invalid).AutoAttachEnabled());
        return Task.CompletedTask;
    }

    [Fact]
    public Task Permits_document_storage_when_it_is_the_explicit_attachment_source_root()
    {
        var valid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DocumentRoot"] = _directory, ["Sap:Mode"] = "write-enabled", ["Sap:EnableAttachments2Writes"] = "true", ["Sap:AutoAttachApprovedDocuments"] = "true", ["Sap:AttachmentSourceRoot"] = _directory
        }).Build();
        Assert.True(new SapAttachmentProcessor(_documents, new RecordingSap(), valid).AutoAttachEnabled());
        return Task.CompletedTask;
    }

    private sealed class RecordingSap : ISapServiceLayerClient
    {
        public (SapDocumentKind Kind, int Entry)? AttachmentRequest { get; private set; }
        public Task AttachPdfAsync(SapDocumentKind kind, int docEntry, int expectedDocNum, string localPdfPath, CancellationToken cancellationToken = default) { AttachmentRequest = (kind, docEntry); return Task.CompletedTask; }
        public Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CheckReadinessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
