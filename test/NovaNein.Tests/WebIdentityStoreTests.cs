using Microsoft.Extensions.Configuration;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class WebIdentityStoreTests : IAsyncLifetime
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"novanein-identities-{Guid.NewGuid():N}");
	private WebIdentityStore _store = null!;

	public async Task InitializeAsync()
	{
		_store = new WebIdentityStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Storage:DatabasePath"] = Path.Combine(_directory, "archive.db")
		}).Build());
		await _store.InitializeAsync();
	}

	public Task DisposeAsync()
	{
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
		return Task.CompletedTask;
	}

	[Fact]
	public async Task Creates_personal_user_with_temporary_password_and_forces_change()
	{
		WebUserProvisioningResult created = await _store.CreateAsync(
			new WebUserRequest("marco", "Alex Operator", "operator@example.invalid", "Operator"),
			"john");

		Assert.NotNull(created.TemporaryPassword);
		Assert.True(created.User.MustChangePassword);
		Assert.Contains(WebPermissions.DocumentsView, created.User.Permissions);
		Assert.DoesNotContain(WebPermissions.DocumentsReview, created.User.Permissions);

		WebUser? authenticated = await _store.AuthenticateAsync("marco", created.TemporaryPassword!, "127.0.0.1");
		Assert.NotNull(authenticated);
		Assert.True(authenticated!.MustChangePassword);

		WebUser changed = await _store.ChangePasswordAsync(authenticated.Id, created.TemporaryPassword!, "EinNeuesSicheresKennwort2026", "127.0.0.1");
		Assert.False(changed.MustChangePassword);
		Assert.NotNull(await _store.AuthenticateAsync("marco", "EinNeuesSicheresKennwort2026", "127.0.0.1"));
	}

	[Fact]
	public async Task Manager_receives_all_permissions()
	{
		WebUserProvisioningResult created = await _store.CreateAsync(
			new WebUserRequest("maintainer", "Example Maintainer", "maintainer@example.invalid", "Manager"),
			"bootstrap");

		Assert.Equal(WebPermissions.Catalog.Count, created.User.Permissions.Count);
		Assert.True(WebPermissions.Has(created.User, WebPermissions.UsersManage));
		Assert.True(WebPermissions.Has(created.User, WebPermissions.InvoicesPost));
	}

	[Fact]
	public async Task Updates_individual_permissions_independently_from_role()
	{
		WebUserProvisioningResult created = await _store.CreateAsync(
			new WebUserRequest("heiner", "Taylor Reviewer", "reviewer@example.invalid", "Operator"),
			"john");

		WebUser updated = await _store.UpdateAsync(
			created.User.Id,
			new WebUserUpdateRequest("Taylor Reviewer", "reviewer@example.invalid", "Operator", [WebPermissions.DocumentsView, WebPermissions.DocumentsReview], true),
			"john");

		Assert.Contains(WebPermissions.DocumentsReview, updated.Permissions);
		Assert.DoesNotContain(WebPermissions.InvoicesView, updated.Permissions);
	}

	[Fact]
	public async Task Records_login_and_administration_events()
	{
		WebUserProvisioningResult created = await _store.CreateAsync(
			new WebUserRequest("audituser", "Audit Benutzer", "audit@example.invalid", "Operator"),
			"john");
		await _store.AuthenticateAsync("audituser", created.TemporaryPassword!, "10.0.0.1");
		await _store.ResetPasswordAsync(created.User.Id, true, "john");

		IReadOnlyList<WebAuthAuditEntry> audit = await _store.ListAuditAsync();
		Assert.Contains(audit, entry => entry.Action == "UserCreated" && entry.UserName == "john");
		Assert.Contains(audit, entry => entry.Action == "LoginSucceeded" && entry.UserName == "audituser");
		Assert.Contains(audit, entry => entry.Action == "PasswordReset" && entry.UserName == "john");
	}
}
