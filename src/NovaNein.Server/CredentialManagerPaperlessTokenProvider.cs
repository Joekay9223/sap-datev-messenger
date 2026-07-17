using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class CredentialManagerPaperlessTokenProvider(IConfiguration configuration) : IPaperlessTokenProvider
{
	public bool TryGetToken(out string token)
	{
		string target = configuration["Integrations:Paperless:CredentialTarget"] ?? "NovaNein/Paperless";
		string userName;
		return WindowsCredentialManager.TryReadSecret(target, out userName, out token);
	}
}
