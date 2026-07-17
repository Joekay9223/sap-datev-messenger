using Microsoft.Extensions.Configuration;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class CockpitWorkItemCurationTests
{
	[Fact]
	public void Keeps_archived_unfinished_documents_visible_outside_the_month_allowlist()
	{
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Cockpit:CuratedOutgoingDocuments:2026-07:0"] = "910810"
		}).Build();
		var curation = new CockpitWorkItemCuration(configuration);
		var hiddenSapRow = Item(910811, null, "pending");
		var archivedFailure = Item(910811, Guid.NewGuid(), "blocked");
		var archivedCompleted = Item(910811, Guid.NewGuid(), "completed");

		Assert.False(curation.IsVisible(hiddenSapRow));
		Assert.True(curation.IsVisible(archivedFailure));
		Assert.False(curation.IsVisible(archivedCompleted));
	}

	private static WorkItem Item(int docNum, Guid? documentId, string overallState)
	{
		WorkItemStage pending = new("pending", "Offen", false);
		WorkItemStages stages = new(pending, pending, pending, pending, pending, pending, pending, pending);
		return new WorkItem(
			"outgoing", "Invoice", docNum, docNum, docNum.ToString(), "Test", new DateOnly(2026, 7, 14),
			100m, "EUR", documentId, documentId.HasValue ? "linked" : "missing", "not-started", "not-prepared",
			"PDF hochladen", true, null, null, new DateOnly(2026, 7, 14), "Ausgangsrechnung", overallState,
			"In Bearbeitung", stages, Array.Empty<WorkItemAction>());
	}
	[Fact]
	public void ConfiguredIncomingMonthOnlyShowsAllowlistedDocuments()
	{
		CockpitWorkItemCuration curation = CreateCuration();

		Assert.True(curation.IsVisible(Item("incoming", 900385, new DateOnly(2026, 7, 1))));
		Assert.False(curation.IsVisible(Item("incoming", 900386, new DateOnly(2026, 7, 1))));
	}

	[Fact]
	public void CurationUsesDocumentDateInsteadOfEntryDate()
	{
		CockpitWorkItemCuration curation = CreateCuration();
		WorkItem juneDocumentEnteredInJuly = Item("incoming", 900484, new DateOnly(2026, 6, 30), new DateOnly(2026, 7, 14));

		Assert.True(curation.IsVisible(juneDocumentEnteredInJuly));
	}

	[Fact]
	public void ConfiguredOutgoingMonthOnlyShowsAllowlistedDocuments()
	{
		CockpitWorkItemCuration curation = CreateCuration();

		Assert.True(curation.IsVisible(Item("outgoing", 910676, new DateOnly(2026, 6, 10))));
		Assert.True(curation.IsVisible(Item("outgoing", 910809, new DateOnly(2026, 7, 13))));
		Assert.False(curation.IsVisible(Item("outgoing", 910772, new DateOnly(2026, 7, 2))));
	}

	[Fact]
	public void UnconfiguredMonthsAreHiddenWhenCuratedListsExist()
	{
		CockpitWorkItemCuration curation = CreateCuration();

		Assert.False(curation.IsVisible(Item("incoming", 900158, new DateOnly(2026, 5, 4))));
		Assert.False(curation.IsVisible(Item("outgoing", 910600, new DateOnly(2026, 5, 4))));
	}

	[Fact]
	public void Shows_every_new_sap_document_from_the_configured_entry_date()
	{
		CockpitWorkItemCuration curation = CreateCuration();
		WorkItem newBackdatedIncoming = Item("incoming", 599999, new DateOnly(2026, 6, 30), new DateOnly(2026, 7, 14));
		WorkItem newOutgoing = Item("outgoing", 199999, new DateOnly(2026, 7, 14), new DateOnly(2026, 7, 14));
		WorkItem historicalNonListed = Item("incoming", 599998, new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 13));

		Assert.True(curation.IsVisible(newBackdatedIncoming));
		Assert.True(curation.IsVisible(newOutgoing));
		Assert.False(curation.IsVisible(historicalNonListed));
	}

	private static CockpitWorkItemCuration CreateCuration()
	{
		Dictionary<string, string?> values = new()
		{
			["Cockpit:CuratedIncomingDocuments:2026-06:0"] = "319503",
			["Cockpit:CuratedIncomingDocuments:2026-06:1"] = "319510",
			["Cockpit:CuratedIncomingDocuments:2026-06:2"] = "900484",
			["Cockpit:CuratedIncomingDocuments:2026-07:0"] = "900385",
			["Cockpit:CuratedOutgoingDocuments:2026-06:0"] = "910643",
			["Cockpit:CuratedOutgoingDocuments:2026-06:1"] = "910676",
			["Cockpit:CuratedOutgoingDocuments:2026-07:0"] = "910809",
			["Cockpit:ShowNewDocumentsFromEntryDate"] = "2026-07-14"
		};
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
		return new CockpitWorkItemCuration(configuration);
	}

	private static WorkItem Item(string direction, int docNum, DateOnly documentDate, DateOnly? entryDate = null)
	{
		WorkItemStage pending = new("pending", "Offen", false);
		WorkItemStages stages = new(pending, pending, pending, pending, pending, pending, pending, pending);
		return new WorkItem(direction, direction == "incoming" ? "PurchaseInvoice" : "Invoice", docNum, docNum, string.Empty, string.Empty, documentDate, null, "EUR", null, "missing", "not-started", "not-prepared", "PDF hochladen", true, null, null, entryDate ?? documentDate, direction == "incoming" ? "Eingangsrechnung" : "Ausgangsrechnung", "pending", "In Bearbeitung", stages);
	}
}
