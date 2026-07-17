using System;
using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record WebUserRequest(
	string UserName,
	string DisplayName,
	string Email,
	string Role,
	IReadOnlyList<string>? Permissions = null,
	string? Password = null,
	bool IsActive = true,
	bool MustChangePassword = true);

public sealed record WebUserUpdateRequest(
	string DisplayName,
	string Email,
	string Role,
	IReadOnlyList<string>? Permissions,
	bool IsActive);

public sealed record WebPasswordChangeRequest(string CurrentPassword, string NewPassword);

public sealed record WebPasswordResetRequest(bool MustChangePassword = true);
