using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace NovaNein.Server;

public sealed class CockpitStatusNotifier(IHubContext<CockpitStatusHub> hub)
{
	public Task ChangedAsync(string kind, object payload, CancellationToken ct = default(CancellationToken))
	{
		return hub.Clients.All.SendAsync("statusChanged", new
		{
			kind = kind,
			payload = payload,
			occurredAt = DateTimeOffset.UtcNow
		}, ct);
	}
}
