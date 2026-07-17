namespace NovaNein.Server;

public sealed class AutomaticOutgoingInvoiceWorker(
	AutomaticBookingStore store,
	InvoiceProposalService proposals,
	IConfiguration configuration,
	ILogger<AutomaticOutgoingInvoiceWorker> logger) : BackgroundService
{
	private bool _configurationWarningLogged;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await ProcessEligibleAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
			catch (Exception exception)
			{
				logger.LogError(exception, "Die automatische Verarbeitung weitergeleiteter Ausgangsrechnungen ist fehlgeschlagen.");
			}
			await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
		}
	}

	internal async Task<int> ProcessEligibleAsync(CancellationToken cancellationToken = default)
	{
		if (!AutomaticOutgoingInvoicePolicy.TryGetActivation(configuration, out var notBefore, out var reason))
		{
			if (configuration.GetValue("Gmail:AutoProcessOutgoingInvoices", false) && !_configurationWarningLogged)
			{
				logger.LogWarning("Ausgangsrechnungs-Automatik bleibt gesperrt: {Reason}", reason);
				_configurationWarningLogged = true;
			}
			return 0;
		}
		_configurationWarningLogged = false;

		var eligible = (await store.ListProposalsAsync(MailSourceStatuses.ProposalReady, cancellationToken))
			.Where(proposal => AutomaticOutgoingInvoicePolicy.IsEligible(proposal, notBefore))
			.OrderBy(proposal => proposal.CreatedAt)
			.ToArray();
		var processed = 0;
		foreach (var proposal in eligible)
		{
			try
			{
				await proposals.ApproveAndPostAsync(
					proposal.Id,
					new InvoiceProposalDecisionRequest(
						proposal.Version,
						"Automatisch freigegeben: grüne Ausgangsrechnung wurde vollständig gegen den vorhandenen SAP-Beleg gelesen."),
					"gmail-outgoing-automation",
					cancellationToken);
				processed++;
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				logger.LogError(exception, "Weitergeleitete Ausgangsrechnung {ProposalId} konnte nicht automatisch verarbeitet werden.", proposal.Id);
			}
		}
		return processed;
	}
}

internal static class AutomaticOutgoingInvoicePolicy
{
	public static bool TryGetActivation(IConfiguration configuration, out DateTimeOffset notBefore, out string reason)
	{
		notBefore = default;
		reason = string.Empty;
		if (!configuration.GetValue("Gmail:AutoProcessOutgoingInvoices", false))
		{
			reason = "Gmail:AutoProcessOutgoingInvoices ist deaktiviert.";
			return false;
		}
		if (!DateTimeOffset.TryParse(configuration["Gmail:AutoProcessOutgoingNotBeforeUtc"], out notBefore))
		{
			reason = "Gmail:AutoProcessOutgoingNotBeforeUtc fehlt oder ist ungültig.";
			return false;
		}
		if (!string.Equals(configuration["Sap:Mode"], "write-enabled", StringComparison.OrdinalIgnoreCase)
			|| !configuration.GetValue("Sap:EnableAttachments2Writes", false)
			|| !configuration.GetValue("Sap:AutoAttachApprovedDocuments", false))
		{
			reason = "Die SAP-Attachments2-Automatik ist nicht vollständig freigegeben.";
			return false;
		}
		if (!AttachmentStorageIsAllowed(configuration))
		{
			reason = "Der zentrale Dokumentordner liegt nicht innerhalb von Sap:AttachmentSourceRoot.";
			return false;
		}
		if (!configuration.GetValue("Datev:AutoPreparePackages", false)
			|| !configuration.GetValue("Datev:TransferAgentEnabled", false)
			|| !string.Equals(configuration["Datev:TransferMode"], "LocalBridge", StringComparison.OrdinalIgnoreCase)
			|| !(configuration.GetValue("Datev:AutoTransferApprovedInvoices", false)
				|| configuration.GetValue("Datev:AutoTransferGreenOnly", false)))
		{
			reason = "DATEV-Paketbildung und automatische Übertragung sind nicht vollständig freigegeben.";
			return false;
		}
		if (!DateTimeOffset.TryParse(configuration["Datev:AutoTransferNotBeforeUtc"], out var datevNotBefore))
		{
			reason = "Datev:AutoTransferNotBeforeUtc fehlt oder ist ungültig.";
			return false;
		}
		if (datevNotBefore > notBefore) notBefore = datevNotBefore;
		return true;
	}

	public static bool IsEligible(InvoiceProposal proposal, DateTimeOffset notBefore) =>
		string.Equals(proposal.Direction, "outgoing", StringComparison.OrdinalIgnoreCase)
		&& string.Equals(proposal.Status, MailSourceStatuses.ProposalReady, StringComparison.Ordinal)
		&& string.Equals(proposal.Signal, "green", StringComparison.OrdinalIgnoreCase)
		&& proposal.CreatedAt >= notBefore;

	private static bool AttachmentStorageIsAllowed(IConfiguration configuration)
	{
		var sourceRoot = configuration["Sap:AttachmentSourceRoot"];
		if (string.IsNullOrWhiteSpace(sourceRoot)) return false;
		try
		{
			var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
			var documents = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuration["Storage:DocumentRoot"] ?? "data/documents"));
			return string.Equals(documents, source, StringComparison.OrdinalIgnoreCase)
				|| documents.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}
}
