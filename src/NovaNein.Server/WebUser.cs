using System;
using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record WebUser(
	Guid Id,
	string UserName,
	string DisplayName,
	string Email,
	string Role,
	IReadOnlyList<string> Permissions,
	bool IsActive,
	bool MustChangePassword,
	int FailedAttempts,
	DateTimeOffset? LockedUntil,
	DateTimeOffset? LastLoginAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

public sealed record WebUserProvisioningResult(WebUser User, string? TemporaryPassword);

public sealed record WebAuthAuditEntry(
	long Id,
	DateTimeOffset OccurredAt,
	string UserName,
	string Action,
	string? RemoteAddress,
	string Detail);
