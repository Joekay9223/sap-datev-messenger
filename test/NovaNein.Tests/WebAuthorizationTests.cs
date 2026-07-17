using System.Security.Claims;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class WebAuthorizationTests
{
	[Fact]
	public void Session_reviewer_needs_explicit_review_permission()
	{
		ClaimsPrincipal reviewer = Principal("Reviewer", "session");

		Assert.False(WebAuthorization.HasReviewerAccess(reviewer));
		reviewer.AddIdentity(new ClaimsIdentity([new Claim("novanein:permission", WebPermissions.DocumentsReview)]));
		Assert.True(WebAuthorization.HasReviewerAccess(reviewer));
	}

	[Fact]
	public void Registered_workstation_keeps_legacy_reviewer_access()
	{
		Assert.True(WebAuthorization.HasReviewerAccess(Principal("Reviewer", "workstation-certificate")));
	}

	[Fact]
	public void Manager_always_has_reviewer_access()
	{
		Assert.True(WebAuthorization.HasReviewerAccess(Principal("Manager", "session")));
	}

	private static ClaimsPrincipal Principal(string role, string access) =>
		new(new ClaimsIdentity(
		[
			new Claim(ClaimTypes.Name, "test"),
			new Claim(ClaimTypes.Role, role),
			new Claim("novanein:access", access)
		],
		"Cookies"));
}
