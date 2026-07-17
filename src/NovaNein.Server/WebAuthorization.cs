using System.Security.Claims;

namespace NovaNein.Server;

public static class WebAuthorization
{
	public static bool HasReviewerAccess(ClaimsPrincipal user)
	{
		if (user.Identity?.IsAuthenticated != true)
		{
			return false;
		}
		if (user.IsInRole("Admin") || user.IsInRole("Manager"))
		{
			return true;
		}
		if (user.HasClaim("novanein:permission", WebPermissions.DocumentsReview))
		{
			return true;
		}
		return string.Equals(user.FindFirstValue("novanein:access"), "workstation-certificate", StringComparison.Ordinal)
			&& user.IsInRole("Reviewer");
	}
}
