using Microsoft.Extensions.Configuration;
using NovaNein.Domain;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class IncomingDocumentIntakeTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-intake-{Guid.NewGuid():N}");
    private string _databasePath = null!;
    private DocumentStore _documents = null!;
    private DocumentJobQueue _jobs = null!;
    private IncomingDocumentIntake _intake = null!;
    private OutgoingDocumentIntake _outgoing = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(_directory, "archive.db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DatabasePath"] = _databasePath }).Build();
        _documents = new DocumentStore(configuration); _jobs = new DocumentJobQueue(configuration); _intake = new IncomingDocumentIntake(_documents, _jobs); _outgoing = new OutgoingDocumentIntake(_documents, _jobs);
        await _documents.InitializeAsync(); await _jobs.InitializeAsync();
    }
    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); return Task.CompletedTask; }

    [Fact]
    public async Task Intake_creates_validation_job_and_reconcile_is_idempotent()
    {
        var document = await _intake.AcceptAsync(new(DocumentDirection.Incoming, 10, 20), new string('A', 64), "invoice.pdf", "workstation");
        Assert.Equal(1, await _intake.ReconcileAsync());
        var job = await _jobs.ClaimNextAsync();
        Assert.NotNull(job);
        Assert.Equal(document.Id, job!.DocumentId);
        Assert.Equal(DocumentJobKind.ValidateIncoming, job.Kind);
        Assert.Null(await _jobs.ClaimNextAsync());
    }

    [Fact]
    public async Task Outgoing_intake_creates_outgoing_validation_job_without_reconciling_as_incoming()
    {
        var document = await _outgoing.AcceptAsync(new(DocumentDirection.Outgoing, 11, 21), new string('B', 64), "outgoing.pdf", "workstation");
        Assert.Equal(0, await _intake.ReconcileAsync());
        Assert.Equal(1, await _outgoing.ReconcileAsync());
        var job = await _jobs.ClaimNextAsync();
        Assert.NotNull(job);
        Assert.Equal(document.Id, job!.DocumentId);
        Assert.Equal(DocumentJobKind.ValidateOutgoing, job.Kind);
    }

    [Fact]
    public async Task Intake_rolls_back_the_document_when_atomic_job_creation_fails()
    {
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_document_job BEFORE INSERT ON document_jobs BEGIN SELECT RAISE(ABORT, 'simulierter Jobfehler'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _intake.AcceptAsync(new(DocumentDirection.Incoming, 12, 22), new string('C', 64), "invoice.pdf", "workstation"));

        Assert.Null(await _documents.GetBySapAsync(DocumentDirection.Incoming, 12));
    }
}
