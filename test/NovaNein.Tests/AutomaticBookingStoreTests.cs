using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class AutomaticBookingStoreTests
{
	[Fact]
	public async Task Persists_gmail_provenance_lines_and_optimistic_proposal_version()
	{
		var root = Path.Combine(Path.GetTempPath(), "NovaNeinTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var store = CreateStore(root);
			await store.InitializeAsync();
			var attachment = new MailAttachmentRecord(
				Guid.NewGuid(), Guid.Empty, "attachment-1", "rechnung.pdf", "application/pdf",
				123, new string('A', 64), Path.Combine(root, "invoice.pdf"), "Ready", null);
			var mail = await store.CreateMailAsync(
				"invoices@example.invalid", "message-1", "thread-1", "history-1",
				"Rechnung", "lieferant@example.test", DateTimeOffset.UtcNow, [attachment]);
			var storedAttachment = Assert.Single(mail.Attachments!);
			var proposal = await store.SaveProposalAsync(CreateProposal(mail.Id, storedAttachment.Id, storedAttachment.Sha256), null);

			Assert.Equal(1, proposal.Version);
			Assert.Equal("V100", proposal.SupplierCode);
			Assert.Equal("4400", Assert.Single(proposal.Lines).Account);
			Assert.Equal("message-1", proposal.MailSource!.GmailMessageId);

			var posting = await store.BeginPostingAsync(
				proposal.Id, proposal.Version, "Fachlich geprüft.", null, "reviewer");
			Assert.Equal(MailSourceStatuses.SapPosting, posting.Status);
			Assert.Equal(2, posting.Version);
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				store.BeginPostingAsync(proposal.Id, proposal.Version, "Veraltete Ansicht.", null, "reviewer"));
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task Rejects_duplicate_attachment_hashes_globally()
	{
		var root = Path.Combine(Path.GetTempPath(), "NovaNeinTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var store = CreateStore(root);
			await store.InitializeAsync();
			var hash = new string('B', 64);
			await store.CreateMailAsync(
				"invoices@example.invalid", "message-a", "thread-a", "history-a", "A", "a@test",
				DateTimeOffset.UtcNow,
				[new MailAttachmentRecord(Guid.NewGuid(), Guid.Empty, "a", "a.pdf", "application/pdf", 1, hash, Path.Combine(root, "a.pdf"), "Ready", null)]);
			Assert.True(await store.HasAttachmentHashAsync(hash));
			Assert.True(await store.HasMailMessageAsync("message-a"));
		}
		finally
		{
			Cleanup(root);
		}
	}

	private static void Cleanup(string root)
	{
		// Microsoft.Data.Sqlite pools native handles by default. Windows keeps the
		// database file locked until the pool is cleared, even though every
		// connection in the store method has already been disposed.
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(root)) Directory.Delete(root, true);
	}

	private static AutomaticBookingStore CreateStore(string root)
		=> new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Storage:DatabasePath"] = Path.Combine(root, "novanein.db")
		}).Build());

	private static InvoiceProposal CreateProposal(Guid mailId, Guid attachmentId, string hash)
	{
		var now = DateTimeOffset.UtcNow;
		return new InvoiceProposal(
			Guid.NewGuid(), mailId, attachmentId, 1, "incoming", MailSourceStatuses.ProposalReady,
			"green", "invoice", "RE-1", "Lieferant GmbH", "V100", "DE123", null, null,
			new DateOnly(2026, 7, 16), null, new DateOnly(2026, 7, 30),
			100m, 19m, 119m, "EUR", false, false, false, "SAP-Historie.", hash,
			now, now, null, null, null, [],
			[new InvoiceProposalLine(1, "Service", 100m, 19m, 19m, "4400", "V2", "SAP-Historie", .95m, false)]);
	}
}
