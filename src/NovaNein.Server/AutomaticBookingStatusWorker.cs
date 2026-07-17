using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class AutomaticBookingStatusWorker(
	AutomaticBookingStore store,
	DocumentStore documents,
	ILogger<AutomaticBookingStatusWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				var proposals = (await store.ListProposalsAsync(null, stoppingToken))
					.Where(item => item.SapPosting != null
						&& item.Status is MailSourceStatuses.SapReadbackConfirmed or MailSourceStatuses.DatevPrepared)
					.ToArray();
				foreach (var proposal in proposals)
				{
					var direction = proposal.Direction == "incoming" ? DocumentDirection.Incoming : DocumentDirection.Outgoing;
					var type = proposal.Direction == "incoming" ? SapBusinessDocumentType.PurchaseInvoice : SapBusinessDocumentType.Invoice;
					var document = await documents.GetBySapAsync(direction, type, proposal.SapPosting!.DocEntry, stoppingToken);
					if (document?.Status == DocumentStatus.Transferred)
						await store.SetDownstreamStatusAsync(proposal.Id, MailSourceStatuses.DatevFinalized, "datev-status-worker", stoppingToken);
					else if (document?.Status == DocumentStatus.Packaged)
						await store.SetDownstreamStatusAsync(proposal.Id, MailSourceStatuses.DatevPrepared, "datev-status-worker", stoppingToken);
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
			catch (Exception exception)
			{
				logger.LogError(exception, "DATEV-Folgestatus der automatischen Buchungen konnte nicht aktualisiert werden.");
			}
			await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
		}
	}
}
