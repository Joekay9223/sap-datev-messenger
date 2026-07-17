using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class CockpitWorkItemCuration
{
	private readonly IReadOnlyDictionary<string, HashSet<int>> _incomingDocNumsByDocumentMonth;
	private readonly IReadOnlyDictionary<string, HashSet<int>> _outgoingDocNumsByDocumentMonth;
	private readonly DateOnly? _showNewDocumentsFromEntryDate;

	public CockpitWorkItemCuration(IConfiguration configuration)
	{
		_incomingDocNumsByDocumentMonth = ReadConfiguredMonths(configuration, "Cockpit:CuratedIncomingDocuments");
		_outgoingDocNumsByDocumentMonth = ReadConfiguredMonths(configuration, "Cockpit:CuratedOutgoingDocuments");
		_showNewDocumentsFromEntryDate = DateOnly.TryParse(
			configuration["Cockpit:ShowNewDocumentsFromEntryDate"],
			CultureInfo.InvariantCulture,
			DateTimeStyles.None,
			out DateOnly newDocumentsFrom)
			? newDocumentsFrom
			: null;
	}

	private static IReadOnlyDictionary<string, HashSet<int>> ReadConfiguredMonths(IConfiguration configuration, string sectionName)
	{
		Dictionary<string, HashSet<int>> configuredMonths = new(StringComparer.OrdinalIgnoreCase);
		foreach (IConfigurationSection month in configuration.GetSection(sectionName).GetChildren())
		{
			HashSet<int> docNums = new();
			foreach (IConfigurationSection value in month.GetChildren())
			{
				if (int.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int docNum) && docNum > 0)
				{
					docNums.Add(docNum);
				}
			}
			if (docNums.Count > 0)
			{
				configuredMonths[month.Key] = docNums;
			}
		}
		return configuredMonths;
	}

	public bool IsVisible(WorkItem item)
	{
		// A curated open-item list may hide SAP rows that are no longer part of the
		// controlling selection. Once NovaNein has archived a PDF, however, an
		// unfinished or failed workflow must remain visible until it is resolved.
		if (item.DocumentId.HasValue && !string.Equals(item.OverallState, "completed", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		// Die kuratierten Listen sind nur der zum Stichtag offene Rückstand. Alle
		// danach in SAP angelegten Belege müssen unabhängig von der Allowlist als
		// neue tägliche Arbeit erscheinen. Maßgeblich ist bewusst das Anlagedatum,
		// nicht ein möglicherweise zurückdatiertes Belegdatum.
		if (_showNewDocumentsFromEntryDate.HasValue
			&& item.EntryDate.HasValue
			&& item.EntryDate.Value >= _showNewDocumentsFromEntryDate.Value)
		{
			return true;
		}
		if (!item.DocumentDate.HasValue)
		{
			return true;
		}
		IReadOnlyDictionary<string, HashSet<int>> configuredMonths;
		if (string.Equals(item.Direction, "incoming", StringComparison.OrdinalIgnoreCase))
		{
			configuredMonths = _incomingDocNumsByDocumentMonth;
		}
		else if (string.Equals(item.Direction, "outgoing", StringComparison.OrdinalIgnoreCase))
		{
			configuredMonths = _outgoingDocNumsByDocumentMonth;
		}
		else
		{
			return true;
		}
		if (configuredMonths.Count == 0)
		{
			return true;
		}
		string month = item.DocumentDate.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture);
		// Vor dem Stichtag bilden ausschließlich die Listen den offenen SAP-Bestand.
		// Nicht konfigurierte historische Monate dürfen nicht wieder den gesamten
		// bereits bereinigten SAP-Korpus einblenden.
		return configuredMonths.TryGetValue(month, out HashSet<int>? allowedDocNums) && allowedDocNums.Contains(item.DocNum);
	}
}
