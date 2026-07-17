using Microsoft.Extensions.Configuration;
using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class IncomingValidationProcessorTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-validation-{Guid.NewGuid():N}");
    private DocumentStore _documents = null!;
    private DocumentJobQueue _jobs = null!;
    private IConfiguration _configuration = null!;

    public async Task InitializeAsync()
    {
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DatabasePath"] = Path.Combine(_directory, "archive.db"), ["Storage:DocumentRoot"] = Path.Combine(_directory, "pdf") }).Build();
        _documents = new DocumentStore(_configuration); await _documents.InitializeAsync();
        _jobs = new DocumentJobQueue(_configuration); await _jobs.InitializeAsync();
    }
    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }

    [Fact]
    public async Task Records_green_validation_from_sap_and_openai_interpretation()
    {
        var hash = new string('C', 64); var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 7, 8), hash, "invoice.pdf", "test");
        Directory.CreateDirectory(_configuration["Storage:DocumentRoot"]!); await File.WriteAllTextAsync(Path.Combine(_configuration["Storage:DocumentRoot"]!, hash + ".pdf"), "fixture");
        var sap = new FakeSap(); var extractor = new FakeExtractor();
        var processor = new IncomingValidationProcessor(_documents, _jobs, sap, extractor, _configuration);
        await processor.ProcessAsync(new(Guid.NewGuid(), document.Id, DocumentJobKind.ValidateIncoming, DocumentJobState.Running, 1, DateTimeOffset.UtcNow, null));
        var validated = await _documents.GetAsync(document.Id);
        Assert.Equal(DocumentStatus.Approved, validated!.Status);
        Assert.Equal(ReviewSignal.Green, validated.Signal);
    }

    [Fact]
    public async Task Recovered_job_treats_an_already_recorded_validation_as_complete()
    {
        var hash = new string('D', 64);
        var document = await _documents.CreateAsync(new(DocumentDirection.Incoming, 9, 10), hash, "invoice.pdf", "test");
        await _documents.RecordValidationAsync(document.Id, new(ReviewSignal.Yellow, ["Manuelle Prüfung erforderlich."]), "validation-worker");
        var processor = new IncomingValidationProcessor(_documents, _jobs, new ThrowingSap(), new ThrowingExtractor(), _configuration);

        await processor.ProcessAsync(new(Guid.NewGuid(), document.Id, DocumentJobKind.ValidateIncoming, DocumentJobState.Running, 2, DateTimeOffset.UtcNow, null));

        var unchanged = await _documents.GetAsync(document.Id);
        Assert.Equal(DocumentStatus.NeedsReview, unchanged!.Status);
        Assert.Single((await _documents.EventsAsync(document.Id)).Where(item => item.Kind == "ValidationCompleted"));
    }

    private sealed class FakeSap : ISapServiceLayerClient
    {
        public Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default) => Task.FromResult(new SapDocumentSnapshot(kind, 7, 8, "V1", "Example Supplier GmbH", "RE-0001", new DateOnly(2026, 7, 10), 119m, "EUR", null));
        public Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SapAttachmentGap>>([]);
        public Task CheckReadinessAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AttachPdfAsync(SapDocumentKind kind, int docEntry, int expectedDocNum, string localPdfPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FakeExtractor : IPdfInvoiceTextExtractor
    {
        public ExtractedInvoiceFacts Extract(string path, DocumentDirection? direction = null) => new("RE 1", "Example Supplier GmbH", null, 119m, "EUR", new DateOnly(2026, 7, 10), true, false, false, "synthetic");
    }
    private sealed class ThrowingSap : ISapServiceLayerClient
    {
        public Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default) => throw new InvalidOperationException("SAP darf beim Wiederanlauf nicht erneut aufgerufen werden.");
        public Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CheckReadinessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AttachPdfAsync(SapDocumentKind kind, int docEntry, int expectedDocNum, string localPdfPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ThrowingExtractor : IPdfInvoiceTextExtractor
    {
        public ExtractedInvoiceFacts Extract(string path, DocumentDirection? direction = null) => throw new InvalidOperationException("PDF darf beim Wiederanlauf nicht erneut gelesen werden.");
    }
}
