using Microsoft.Extensions.Configuration;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class CompositeSapClientTests
{
    [Fact]
    public async Task Sql_read_only_mode_delegates_all_reads_to_sql_but_attachments_to_service_layer()
    {
        var serviceLayer = new FakeServiceLayerClient();
        var sql = new FakeSqlReadClient();
        var client = new CompositeSapClient(serviceLayer, sql, Configuration("sql-read-only"));

        await client.GetDocumentAsync(SapDocumentKind.PurchaseInvoice, 17);
        await client.FindMissingPdfAttachmentsAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));
        await client.CheckReadinessAsync();
        await client.FindSuppliersAsync("Lieferant", null, null, null, null, null, null);
        await client.GetSupplierCodingHistoryAsync("L1000");
        await client.ValidateAccountAsync("6850");
        await client.FindPurchaseInvoiceDuplicateAsync("L1000", "RE-1");
        await client.AttachPdfAsync(SapDocumentKind.PurchaseInvoice, 17, 42, "approved.pdf");

        Assert.Equal(1, sql.GetDocumentCalls);
        Assert.Equal(1, sql.MissingAttachmentCalls);
        Assert.Equal(1, sql.ReadinessCalls);
        Assert.Equal(1, sql.SupplierCalls);
        Assert.Equal(1, sql.CodingCalls);
        Assert.Equal(1, sql.AccountCalls);
        Assert.Equal(1, sql.DuplicateCalls);
        Assert.Equal(0, serviceLayer.GetDocumentCalls);
        Assert.Equal(0, serviceLayer.MissingAttachmentCalls);
        Assert.Equal(0, serviceLayer.ReadinessCalls);
        Assert.Equal(0, serviceLayer.SupplierCalls);
        Assert.Equal(0, serviceLayer.CodingCalls);
        Assert.Equal(0, serviceLayer.AccountCalls);
        Assert.Equal(0, serviceLayer.DuplicateCalls);
        Assert.Equal(1, serviceLayer.AttachCalls);
    }

    [Fact]
    public async Task Default_mode_keeps_every_operation_on_service_layer()
    {
        var serviceLayer = new FakeServiceLayerClient();
        var sql = new FakeSqlReadClient();
        var client = new CompositeSapClient(serviceLayer, sql, new ConfigurationBuilder().Build());

        await client.GetDocumentAsync(SapDocumentKind.Invoice, 17);
        await client.FindMissingPdfAttachmentsAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));
        await client.CheckReadinessAsync();
        await client.FindSuppliersAsync("Lieferant", null, null, null, null, null, null);
        await client.GetSupplierCodingHistoryAsync("L1000");
        await client.ValidateAccountAsync("6850");
        await client.FindPurchaseInvoiceDuplicateAsync("L1000", "RE-1");
        await client.AttachPdfAsync(SapDocumentKind.Invoice, 17, 42, "approved.pdf");

        Assert.Equal(1, serviceLayer.GetDocumentCalls);
        Assert.Equal(1, serviceLayer.MissingAttachmentCalls);
        Assert.Equal(1, serviceLayer.ReadinessCalls);
        Assert.Equal(1, serviceLayer.SupplierCalls);
        Assert.Equal(1, serviceLayer.CodingCalls);
        Assert.Equal(1, serviceLayer.AccountCalls);
        Assert.Equal(1, serviceLayer.DuplicateCalls);
        Assert.Equal(1, serviceLayer.AttachCalls);
        Assert.Equal(0, sql.GetDocumentCalls);
        Assert.Equal(0, sql.MissingAttachmentCalls);
        Assert.Equal(0, sql.ReadinessCalls);
        Assert.Equal(0, sql.SupplierCalls);
        Assert.Equal(0, sql.CodingCalls);
        Assert.Equal(0, sql.AccountCalls);
        Assert.Equal(0, sql.DuplicateCalls);
    }

    [Fact]
    public async Task Unknown_read_mode_fails_closed_instead_of_silently_selecting_a_backend()
    {
        var client = new CompositeSapClient(
            new FakeServiceLayerClient(),
            new FakeSqlReadClient(),
            Configuration("sql-write"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetDocumentAsync(SapDocumentKind.Invoice, 17));

        Assert.Contains("Sap:ReadMode", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(string readMode) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sap:ReadMode"] = readMode
        }).Build();

    private sealed class FakeServiceLayerClient : ISapServiceLayerClient
    {
        public int GetDocumentCalls { get; private set; }
        public int MissingAttachmentCalls { get; private set; }
        public int ReadinessCalls { get; private set; }
        public int AttachCalls { get; private set; }
        public int SupplierCalls { get; private set; }
        public int CodingCalls { get; private set; }
        public int AccountCalls { get; private set; }
        public int DuplicateCalls { get; private set; }

        public Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default)
        {
            GetDocumentCalls++;
            return Task.FromResult(Document(kind, docEntry));
        }

        public Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default)
        {
            MissingAttachmentCalls++;
            return Task.FromResult<IReadOnlyList<SapAttachmentGap>>([]);
        }

        public Task CheckReadinessAsync(CancellationToken cancellationToken = default)
        {
            ReadinessCalls++;
            return Task.CompletedTask;
        }

        public Task AttachPdfAsync(SapDocumentKind kind, int docEntry, int expectedDocNum, string localPdfPath, CancellationToken cancellationToken = default)
        {
            AttachCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SapSupplierCandidate>> FindSuppliersAsync(string name, string? vatId, string? taxNumber, string? iban, string? street, string? postalCode, string? city, CancellationToken cancellationToken = default)
        {
            SupplierCalls++;
            return Task.FromResult<IReadOnlyList<SapSupplierCandidate>>([]);
        }

        public Task<IReadOnlyList<SapCodingCandidate>> GetSupplierCodingHistoryAsync(string cardCode, CancellationToken cancellationToken = default)
        {
            CodingCalls++;
            return Task.FromResult<IReadOnlyList<SapCodingCandidate>>([]);
        }

        public Task<SapAccountValidation> ValidateAccountAsync(string account, CancellationToken cancellationToken = default)
        {
            AccountCalls++;
            return Task.FromResult(new SapAccountValidation(account, true, true, false, "Testkonto", null));
        }

        public Task<SapDocumentSnapshot?> FindPurchaseInvoiceDuplicateAsync(string cardCode, string invoiceNumber, CancellationToken cancellationToken = default)
        {
            DuplicateCalls++;
            return Task.FromResult<SapDocumentSnapshot?>(null);
        }
    }

    private sealed class FakeSqlReadClient : ISapSqlReadClient
    {
        public int GetDocumentCalls { get; private set; }
        public int MissingAttachmentCalls { get; private set; }
        public int ReadinessCalls { get; private set; }
        public int SupplierCalls { get; private set; }
        public int CodingCalls { get; private set; }
        public int AccountCalls { get; private set; }
        public int DuplicateCalls { get; private set; }

        public Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default)
        {
            GetDocumentCalls++;
            return Task.FromResult(Document(kind, docEntry));
        }

        public Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default)
        {
            MissingAttachmentCalls++;
            return Task.FromResult<IReadOnlyList<SapAttachmentGap>>([]);
        }

        public Task CheckReadinessAsync(CancellationToken cancellationToken = default)
        {
            ReadinessCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SapSupplierCandidate>> FindSuppliersAsync(string name, string? vatId, string? taxNumber, string? iban, string? street, string? postalCode, string? city, CancellationToken cancellationToken = default)
        {
            SupplierCalls++;
            return Task.FromResult<IReadOnlyList<SapSupplierCandidate>>([]);
        }

        public Task<IReadOnlyList<SapCodingCandidate>> GetSupplierCodingHistoryAsync(string cardCode, CancellationToken cancellationToken = default)
        {
            CodingCalls++;
            return Task.FromResult<IReadOnlyList<SapCodingCandidate>>([]);
        }

        public Task<SapAccountValidation> ValidateAccountAsync(string account, CancellationToken cancellationToken = default)
        {
            AccountCalls++;
            return Task.FromResult(new SapAccountValidation(account, true, true, false, "Testkonto", null));
        }

        public Task<SapDocumentSnapshot?> FindPurchaseInvoiceDuplicateAsync(string cardCode, string invoiceNumber, CancellationToken cancellationToken = default)
        {
            DuplicateCalls++;
            return Task.FromResult<SapDocumentSnapshot?>(null);
        }
    }

    private static SapDocumentSnapshot Document(SapDocumentKind kind, int docEntry) =>
        new(kind, docEntry, 42, "C1", "Example Company-Test", "42", new DateOnly(2026, 7, 1), 119m, "EUR", null);
}
