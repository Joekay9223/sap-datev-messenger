using Microsoft.Extensions.Configuration;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class AutomaticOutgoingInvoicePolicyTests
{
	[Fact]
	public void Requires_all_sap_datev_and_activation_gates()
	{
		var configuration = Configuration(new Dictionary<string, string?>
		{
			["Gmail:AutoProcessOutgoingInvoices"] = "true"
		});

		Assert.False(AutomaticOutgoingInvoicePolicy.TryGetActivation(configuration, out _, out var reason));
		Assert.Contains("NotBeforeUtc", reason, StringComparison.Ordinal);
	}

	[Fact]
	public void Activates_only_new_green_outgoing_proposals_after_the_cutoff()
	{
		var cutoff = DateTimeOffset.Parse("2026-07-17T10:00:00Z");
		var attachmentRoot = Path.Combine(Path.GetTempPath(), "novanein-documents");
		var configuration = Configuration(new Dictionary<string, string?>
		{
			["Gmail:AutoProcessOutgoingInvoices"] = "true",
			["Gmail:AutoProcessOutgoingNotBeforeUtc"] = cutoff.ToString("O"),
			["Sap:Mode"] = "write-enabled",
			["Sap:EnableAttachments2Writes"] = "true",
			["Sap:AutoAttachApprovedDocuments"] = "true",
			["Sap:AttachmentSourceRoot"] = attachmentRoot,
			["Storage:DocumentRoot"] = attachmentRoot,
			["Datev:AutoPreparePackages"] = "true",
			["Datev:TransferAgentEnabled"] = "true",
			["Datev:TransferMode"] = "LocalBridge",
			["Datev:AutoTransferApprovedInvoices"] = "true",
			["Datev:AutoTransferNotBeforeUtc"] = cutoff.ToString("O")
		});
		var proposal = Proposal(cutoff.AddSeconds(1));

		Assert.True(AutomaticOutgoingInvoicePolicy.TryGetActivation(configuration, out var activeFrom, out _));
		Assert.Equal(cutoff, activeFrom);
		Assert.True(AutomaticOutgoingInvoicePolicy.IsEligible(proposal, activeFrom));
		Assert.False(AutomaticOutgoingInvoicePolicy.IsEligible(proposal with { CreatedAt = cutoff.AddSeconds(-1) }, activeFrom));
		Assert.False(AutomaticOutgoingInvoicePolicy.IsEligible(proposal with { Direction = "incoming" }, activeFrom));
		Assert.False(AutomaticOutgoingInvoicePolicy.IsEligible(proposal with { Signal = "yellow" }, activeFrom));
	}

	private static IConfiguration Configuration(Dictionary<string, string?> values) =>
		new ConfigurationBuilder().AddInMemoryCollection(values).Build();

	private static InvoiceProposal Proposal(DateTimeOffset createdAt) => new(
		Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "outgoing", MailSourceStatuses.ProposalReady,
		"green", "invoice", "900003", "Example Customer GmbH", null, "DE999", null, null,
		new DateOnly(2026, 7, 17), null, null, 100m, 19m, 119m, "EUR",
		false, true, false, "SAP vollständig gelesen.", new string('A', 64), createdAt, createdAt,
		null, null, null, [],
		[new InvoiceProposalLine(1, "Ware", 100m, 19m, 19m, "8400", "A2", "SAP-Readback", 1m, false)]);
}
