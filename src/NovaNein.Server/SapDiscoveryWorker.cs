using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NovaNein.Server;

public sealed class SapDiscoveryWorker(WorkItemService workItems, CockpitStatusNotifier notifier, IConfiguration configuration, ILogger<SapDiscoveryWorker> logger) : BackgroundService()
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		string previous = null;
		int configured = configuration.GetValue("Cockpit:SapDiscoveryIntervalSeconds", 30);
		TimeSpan interval = TimeSpan.FromSeconds(Math.Clamp(configured, 15, 300));
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				string fingerprint = await FingerprintAsync(stoppingToken);
				if (previous != null && !string.Equals(previous, fingerprint, StringComparison.Ordinal))
				{
					await notifier.ChangedAsync("SapWorkItemsChanged", new
					{
						reload = true
					}, stoppingToken);
				}
				previous = fingerprint;
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "SAP-Discovery konnte den Cockpitstand nicht aktualisieren.");
			}
			await Task.Delay(interval, stoppingToken);
		}
	}

	private async Task<string> FingerprintAsync(CancellationToken cancellationToken)
	{
		StringBuilder builder = new StringBuilder();
		int page = 1;
		while (true)
		{
			WorkItemService workItemService = workItems;
			int page2 = page;
			WorkItemPage current = await workItemService.ListAsync(new WorkItemQuery(null, null, null, null, null, null, null, page2, 200), cancellationToken);
			foreach (WorkItem item in current.Items.OrderBy<WorkItem, string>((WorkItem x) => x.SapKind, StringComparer.Ordinal).ThenBy((WorkItem x) => x.DocEntry))
			{
				builder.Append(item.SapKind).Append('|').Append(item.DocEntry)
					.Append('|')
					.Append(item.PdfState)
					.Append('|')
					.Append(item.ValidationState)
					.Append('|')
					.Append(item.DatevState)
					.Append('|')
					.Append(item.UpdatedAt?.ToUnixTimeSeconds())
					.Append('\n');
			}
			if (!current.NextPage.HasValue)
			{
				break;
			}
			page = current.NextPage.Value;
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	}
}
