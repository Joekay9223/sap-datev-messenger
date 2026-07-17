using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NovaNein.Server;

public sealed class TrustedNetworkAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
	public const string SchemeName = "NovaNein.TrustedNetwork";

	private readonly IConfiguration _configuration;

	public TrustedNetworkAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
		: base(options, logger, encoder)
	{
		_configuration = configuration;
	}

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		if (!string.Equals(_configuration["WebAccess:Mode"], "TrustedNetwork", StringComparison.OrdinalIgnoreCase))
		{
			return Task.FromResult(AuthenticateResult.NoResult());
		}
		IPAddress remote = base.Context.Connection.RemoteIpAddress;
		if (remote == null)
		{
			return Task.FromResult(AuthenticateResult.Fail("Die Quelladresse fehlt."));
		}
		if (remote.IsIPv4MappedToIPv6)
		{
			remote = remote.MapToIPv4();
		}
		bool isLoopback = IPAddress.IsLoopback(remote);
		bool isLan = AllowedNetworks(_configuration).Any((CidrNetwork network) => network.Contains(remote));
		if (!isLoopback && !isLan)
		{
			return Task.FromResult(AuthenticateResult.Fail("Der Zugriff ist nur aus dem freigegebenen Firmennetz erlaubt."));
		}
		string tailscaleLogin = (isLoopback ? base.Request.Headers["Tailscale-User-Login"].FirstOrDefault() : null);
		string actor = ((!string.IsNullOrWhiteSpace(tailscaleLogin)) ? ("tailscale:" + SanitizeActor(tailscaleLogin)) : $"lan:{remote}");
		Claim[] claims = new Claim[4]
		{
			new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", actor),
			new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", actor),
			new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "Admin"),
			new Claim("novanein:access", isLoopback ? "tailscale-proxy" : "lan")
		};
		ClaimsPrincipal principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "NovaNein.TrustedNetwork"));
		return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "NovaNein.TrustedNetwork")));
	}

	protected override Task HandleChallengeAsync(AuthenticationProperties properties)
	{
		base.Response.StatusCode = 403;
		return base.Response.WriteAsJsonAsync(new
		{
			error = "NovaNein ist nur aus dem freigegebenen Firmennetz erreichbar."
		});
	}

	internal static IReadOnlyList<CidrNetwork> AllowedNetworks(IConfiguration configuration)
	{
		string[] values = configuration.GetSection("WebAccess:AllowedCidrs").Get<string[]>() ?? Array.Empty<string>();
		return values.Select(CidrNetwork.Parse).ToArray();
	}

	private static string SanitizeActor(string value)
	{
		string safe = new string((from ch in value.Trim()
			where !char.IsControl(ch)
			select ch).Take(200).ToArray());
		if (!string.IsNullOrWhiteSpace(safe))
		{
			return safe;
		}
		return "unbekannt";
	}
}
