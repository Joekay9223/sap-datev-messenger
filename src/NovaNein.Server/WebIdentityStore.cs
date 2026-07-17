using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class WebIdentityStore
{
	private const int Iterations = 600000;
	private const int LockoutThreshold = 5;
	private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
	private readonly string _connectionString;

	public WebIdentityStore(IConfiguration configuration)
	{
		_connectionString = "Data Source=" + (configuration["Storage:DatabasePath"] ?? "data/novanein.db");
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		string? directory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(_connectionString).DataSource);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}

		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		await using (SqliteCommand command = connection.CreateCommand())
		{
			command.CommandText = """
				CREATE TABLE IF NOT EXISTS web_users (
				  id TEXT PRIMARY KEY,
				  user_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
				  password_hash TEXT NOT NULL,
				  role TEXT NOT NULL,
				  is_active INTEGER NOT NULL DEFAULT 1,
				  failed_attempts INTEGER NOT NULL DEFAULT 0,
				  locked_until TEXT NULL,
				  created_at TEXT NOT NULL,
				  updated_at TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS web_auth_audit (
				  id INTEGER PRIMARY KEY AUTOINCREMENT,
				  occurred_at TEXT NOT NULL,
				  user_name TEXT NOT NULL,
				  action TEXT NOT NULL,
				  remote_address TEXT NULL,
				  detail TEXT NOT NULL
				);
				CREATE INDEX IF NOT EXISTS ix_web_auth_audit_occurred_at ON web_auth_audit(occurred_at DESC);
				""";
			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		await EnsureColumnAsync(connection, "display_name", "TEXT NOT NULL DEFAULT ''", cancellationToken);
		await EnsureColumnAsync(connection, "email", "TEXT NOT NULL DEFAULT ''", cancellationToken);
		await EnsureColumnAsync(connection, "permissions_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
		await EnsureColumnAsync(connection, "must_change_password", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
		await EnsureColumnAsync(connection, "last_login_at", "TEXT NULL", cancellationToken);
		await EnsureColumnAsync(connection, "created_by", "TEXT NULL", cancellationToken);

		await using SqliteCommand migrate = connection.CreateCommand();
		migrate.CommandText = """
			UPDATE web_users
			SET display_name = CASE WHEN trim(display_name) = '' THEN user_name ELSE display_name END,
			    permissions_json = CASE
			      WHEN permissions_json IS NULL OR trim(permissions_json) = '' OR permissions_json = '[]' THEN
			        CASE role
			          WHEN 'Operator' THEN '["documents.view","invoices.view","accounting.view","audit.view","paperless.view"]'
			          WHEN 'Reviewer' THEN '["documents.view","documents.review","invoices.view","invoices.post","accounting.view","accounting.manage","audit.view","paperless.view"]'
			          WHEN 'MasterDataApprover' THEN '["documents.view","invoices.view","suppliers.manage","accounting.view","audit.view","paperless.view"]'
			          WHEN 'Admin' THEN '["documents.view","documents.review","invoices.view","invoices.post","suppliers.manage","accounting.view","accounting.manage","audit.view","users.manage","integrations.manage","paperless.view"]'
			          WHEN 'Manager' THEN '["documents.view","documents.review","invoices.view","invoices.post","suppliers.manage","accounting.view","accounting.manage","audit.view","users.manage","integrations.manage","paperless.view"]'
			          ELSE '["documents.view","invoices.view","accounting.view","audit.view","paperless.view"]'
			        END
			      ELSE permissions_json
			    END;
			""";
		await migrate.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task<WebUser?> AuthenticateAsync(string userName, string password, string? remoteAddress, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
		{
			return null;
		}

		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		WebUserRow? row = await ReadRowByUserNameAsync(connection, userName.Trim(), cancellationToken);
		if (row == null)
		{
			await AuditAsync(connection, userName.Trim(), "LoginFailed", remoteAddress, "Benutzer unbekannt.", cancellationToken);
			return null;
		}

		if (!row.User.IsActive || row.User.LockedUntil is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow)
		{
			await AuditAsync(connection, row.User.UserName, "LoginBlocked", remoteAddress, "Benutzer deaktiviert oder gesperrt.", cancellationToken);
			return null;
		}

		if (!VerifyPassword(password, row.PasswordHash))
		{
			int failed = row.User.FailedAttempts + 1;
			DateTimeOffset? lockUntil = failed >= LockoutThreshold ? DateTimeOffset.UtcNow.Add(LockoutDuration) : null;
			await UpdateFailureAsync(connection, row.User.Id, failed, lockUntil, cancellationToken);
			await AuditAsync(connection, row.User.UserName, "LoginFailed", remoteAddress, lockUntil.HasValue ? "Konto vorübergehend gesperrt." : "Kennwort falsch.", cancellationToken);
			return null;
		}

		DateTimeOffset loginAt = DateTimeOffset.UtcNow;
		await UpdateSuccessAsync(connection, row.User.Id, loginAt, cancellationToken);
		await AuditAsync(connection, row.User.UserName, "LoginSucceeded", remoteAddress, "Web-Cockpit-Anmeldung erfolgreich.", cancellationToken);
		return row.User with { FailedAttempts = 0, LockedUntil = null, LastLoginAt = loginAt, UpdatedAt = loginAt };
	}

	public Task<WebUser> CreateOrReplaceAdminAsync(string userName, string password, CancellationToken cancellationToken = default) =>
		CreateOrReplaceAsync(userName, password, "Admin", cancellationToken);

	public async Task<WebUser> CreateOrReplaceAsync(string userName, string password, string role, CancellationToken cancellationToken = default, string? actor = null)
	{
		ValidateRole(role);
		ValidateCredentials(userName, password);
		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		WebUserRow? existing = await ReadRowByUserNameAsync(connection, userName.Trim(), cancellationToken);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		Guid id = existing?.User.Id ?? Guid.NewGuid();
		string displayName = existing?.User.DisplayName ?? userName.Trim();
		string email = existing?.User.Email ?? "";
		IReadOnlyList<string> permissions = WebPermissions.DefaultsForRole(role);

		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO web_users(id,user_name,display_name,email,password_hash,role,permissions_json,is_active,must_change_password,failed_attempts,locked_until,last_login_at,created_by,created_at,updated_at)
			VALUES($id,$name,$display,$email,$hash,$role,$permissions,1,0,0,NULL,$lastLogin,$actor,$created,$now)
			ON CONFLICT(user_name) DO UPDATE SET
			  display_name=$display,email=$email,password_hash=$hash,role=$role,permissions_json=$permissions,
			  is_active=1,must_change_password=0,failed_attempts=0,locked_until=NULL,updated_at=$now;
			""";
		command.Parameters.AddWithValue("$id", id.ToString());
		command.Parameters.AddWithValue("$name", userName.Trim());
		command.Parameters.AddWithValue("$display", displayName);
		command.Parameters.AddWithValue("$email", email);
		command.Parameters.AddWithValue("$hash", HashPassword(password));
		command.Parameters.AddWithValue("$role", role);
		command.Parameters.AddWithValue("$permissions", SerializePermissions(permissions));
		command.Parameters.AddWithValue("$lastLogin", existing?.User.LastLoginAt?.ToString("O") is { } lastLogin ? lastLogin : DBNull.Value);
		command.Parameters.AddWithValue("$actor", actor ?? userName.Trim());
		command.Parameters.AddWithValue("$created", existing?.User.CreatedAt.ToString("O") ?? now.ToString("O"));
		command.Parameters.AddWithValue("$now", now.ToString("O"));
		await command.ExecuteNonQueryAsync(cancellationToken);

		WebUser result = (await ReadRowByUserNameAsync(connection, userName.Trim(), cancellationToken))!.User;
		await AuditAsync(connection, actor ?? result.UserName, "UserProvisioned", null, $"Web-Benutzer {result.UserName} mit Rolle {result.Role} eingerichtet.", cancellationToken);
		return result;
	}

	public async Task<WebUserProvisioningResult> CreateAsync(WebUserRequest request, string actor, CancellationToken cancellationToken = default)
	{
		ValidateProfile(request.UserName, request.DisplayName, request.Email, request.Role);
		IReadOnlyList<string> permissions = WebPermissions.Normalize(request.Permissions, request.Role);
		string password = string.IsNullOrEmpty(request.Password) ? GenerateTemporaryPassword() : request.Password;
		ValidateCredentials(request.UserName, password);

		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		if (await ReadRowByUserNameAsync(connection, request.UserName.Trim(), cancellationToken) != null)
		{
			throw new InvalidOperationException("Ein Benutzer mit diesem Benutzernamen ist bereits vorhanden.");
		}

		DateTimeOffset now = DateTimeOffset.UtcNow;
		Guid id = Guid.NewGuid();
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO web_users(id,user_name,display_name,email,password_hash,role,permissions_json,is_active,must_change_password,failed_attempts,locked_until,last_login_at,created_by,created_at,updated_at)
			VALUES($id,$name,$display,$email,$hash,$role,$permissions,$active,$mustChange,0,NULL,NULL,$actor,$now,$now);
			""";
		command.Parameters.AddWithValue("$id", id.ToString());
		command.Parameters.AddWithValue("$name", request.UserName.Trim());
		command.Parameters.AddWithValue("$display", request.DisplayName.Trim());
		command.Parameters.AddWithValue("$email", request.Email.Trim());
		command.Parameters.AddWithValue("$hash", HashPassword(password));
		command.Parameters.AddWithValue("$role", request.Role);
		command.Parameters.AddWithValue("$permissions", SerializePermissions(permissions));
		command.Parameters.AddWithValue("$active", request.IsActive ? 1 : 0);
		command.Parameters.AddWithValue("$mustChange", request.MustChangePassword ? 1 : 0);
		command.Parameters.AddWithValue("$actor", actor);
		command.Parameters.AddWithValue("$now", now.ToString("O"));
		await command.ExecuteNonQueryAsync(cancellationToken);

		WebUser user = (await ReadRowByIdAsync(connection, id, cancellationToken))!.User;
		await AuditAsync(connection, actor, "UserCreated", null, $"Benutzer {user.UserName} ({user.DisplayName}) mit Rolle {user.Role} angelegt.", cancellationToken);
		return new WebUserProvisioningResult(user, string.IsNullOrEmpty(request.Password) ? password : null);
	}

	public async Task<WebUser> UpdateAsync(Guid id, WebUserUpdateRequest request, string actor, CancellationToken cancellationToken = default)
	{
		ValidateProfile("user", request.DisplayName, request.Email, request.Role, validateUserName: false);
		IReadOnlyList<string> permissions = WebPermissions.Normalize(request.Permissions, request.Role);
		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		WebUserRow? existing = await ReadRowByIdAsync(connection, id, cancellationToken);
		if (existing == null)
		{
			throw new KeyNotFoundException("Der Benutzer wurde nicht gefunden.");
		}
		if (string.Equals(existing.User.UserName, actor, StringComparison.OrdinalIgnoreCase) && !request.IsActive)
		{
			throw new InvalidOperationException("Das eigene Benutzerkonto kann nicht deaktiviert werden.");
		}

		DateTimeOffset now = DateTimeOffset.UtcNow;
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			UPDATE web_users SET display_name=$display,email=$email,role=$role,permissions_json=$permissions,
			  is_active=$active,failed_attempts=CASE WHEN $active=1 THEN 0 ELSE failed_attempts END,
			  locked_until=CASE WHEN $active=1 THEN NULL ELSE locked_until END,updated_at=$now
			WHERE id=$id;
			""";
		command.Parameters.AddWithValue("$display", request.DisplayName.Trim());
		command.Parameters.AddWithValue("$email", request.Email.Trim());
		command.Parameters.AddWithValue("$role", request.Role);
		command.Parameters.AddWithValue("$permissions", SerializePermissions(permissions));
		command.Parameters.AddWithValue("$active", request.IsActive ? 1 : 0);
		command.Parameters.AddWithValue("$now", now.ToString("O"));
		command.Parameters.AddWithValue("$id", id.ToString());
		await command.ExecuteNonQueryAsync(cancellationToken);

		WebUser user = (await ReadRowByIdAsync(connection, id, cancellationToken))!.User;
		await AuditAsync(connection, actor, "UserUpdated", null, $"Benutzer {user.UserName}: Rolle {user.Role}, aktiv={user.IsActive}, Berechtigungen={string.Join(",", user.Permissions)}.", cancellationToken);
		return user;
	}

	public async Task<WebUserProvisioningResult> ResetPasswordAsync(Guid id, bool mustChangePassword, string actor, CancellationToken cancellationToken = default)
	{
		string password = GenerateTemporaryPassword();
		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		WebUserRow? existing = await ReadRowByIdAsync(connection, id, cancellationToken);
		if (existing == null)
		{
			throw new KeyNotFoundException("Der Benutzer wurde nicht gefunden.");
		}

		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE web_users SET password_hash=$hash,must_change_password=$mustChange,failed_attempts=0,locked_until=NULL,updated_at=$now WHERE id=$id";
		command.Parameters.AddWithValue("$hash", HashPassword(password));
		command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
		command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
		command.Parameters.AddWithValue("$id", id.ToString());
		await command.ExecuteNonQueryAsync(cancellationToken);
		WebUser user = (await ReadRowByIdAsync(connection, id, cancellationToken))!.User;
		await AuditAsync(connection, actor, "PasswordReset", null, $"Temporäres Kennwort für {user.UserName} erzeugt.", cancellationToken);
		return new WebUserProvisioningResult(user, password);
	}

	public async Task<WebUser> ChangePasswordAsync(Guid id, string currentPassword, string newPassword, string? remoteAddress, CancellationToken cancellationToken = default)
	{
		ValidateNewPassword(newPassword);
		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		WebUserRow? existing = await ReadRowByIdAsync(connection, id, cancellationToken);
		if (existing == null || !VerifyPassword(currentPassword, existing.PasswordHash))
		{
			if (existing != null)
			{
				await AuditAsync(connection, existing.User.UserName, "PasswordChangeFailed", remoteAddress, "Aktuelles Kennwort war nicht korrekt.", cancellationToken);
			}
			throw new UnauthorizedAccessException("Das aktuelle Kennwort ist nicht korrekt.");
		}
		if (VerifyPassword(newPassword, existing.PasswordHash))
		{
			throw new ArgumentException("Das neue Kennwort muss sich vom bisherigen Kennwort unterscheiden.", nameof(newPassword));
		}

		DateTimeOffset now = DateTimeOffset.UtcNow;
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE web_users SET password_hash=$hash,must_change_password=0,failed_attempts=0,locked_until=NULL,updated_at=$now WHERE id=$id";
		command.Parameters.AddWithValue("$hash", HashPassword(newPassword));
		command.Parameters.AddWithValue("$now", now.ToString("O"));
		command.Parameters.AddWithValue("$id", id.ToString());
		await command.ExecuteNonQueryAsync(cancellationToken);
		await AuditAsync(connection, existing.User.UserName, "PasswordChanged", remoteAddress, "Kennwort wurde geändert.", cancellationToken);
		return (await ReadRowByIdAsync(connection, id, cancellationToken))!.User;
	}

	public async Task<IReadOnlyList<WebUser>> ListAsync(CancellationToken cancellationToken = default)
	{
		List<WebUser> users = [];
		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = UserSelect + " ORDER BY display_name COLLATE NOCASE,user_name COLLATE NOCASE";
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			users.Add(ReadUser(reader));
		}
		return users;
	}

	public async Task<IReadOnlyList<WebAuthAuditEntry>> ListAuditAsync(int limit = 100, CancellationToken cancellationToken = default)
	{
		List<WebAuthAuditEntry> entries = [];
		await using SqliteConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT id,occurred_at,user_name,action,remote_address,detail FROM web_auth_audit ORDER BY id DESC LIMIT $limit";
		command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			entries.Add(new WebAuthAuditEntry(
				reader.GetInt64(0),
				DateTimeOffset.Parse(reader.GetString(1)),
				reader.GetString(2),
				reader.GetString(3),
				reader.IsDBNull(4) ? null : reader.GetString(4),
				reader.GetString(5)));
		}
		return entries;
	}

	private const string UserSelect = """
		SELECT id,user_name,display_name,email,password_hash,role,permissions_json,is_active,must_change_password,
		       failed_attempts,locked_until,last_login_at,created_at,updated_at
		FROM web_users
		""";

	private static async Task<WebUserRow?> ReadRowByUserNameAsync(SqliteConnection connection, string userName, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = UserSelect + " WHERE user_name=$name";
		command.Parameters.AddWithValue("$name", userName);
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		return await reader.ReadAsync(cancellationToken) ? new WebUserRow(ReadUser(reader), reader.GetString(4)) : null;
	}

	private static async Task<WebUserRow?> ReadRowByIdAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = UserSelect + " WHERE id=$id";
		command.Parameters.AddWithValue("$id", id.ToString());
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		return await reader.ReadAsync(cancellationToken) ? new WebUserRow(ReadUser(reader), reader.GetString(4)) : null;
	}

	private static WebUser ReadUser(SqliteDataReader reader)
	{
		string role = reader.GetString(5);
		IReadOnlyList<string> permissions = ParsePermissions(reader.IsDBNull(6) ? null : reader.GetString(6), role);
		return new WebUser(
			Guid.Parse(reader.GetString(0)),
			reader.GetString(1),
			string.IsNullOrWhiteSpace(reader.GetString(2)) ? reader.GetString(1) : reader.GetString(2),
			reader.GetString(3),
			role,
			permissions,
			reader.GetInt32(7) != 0,
			reader.GetInt32(8) != 0,
			reader.GetInt32(9),
			reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
			reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
			DateTimeOffset.Parse(reader.GetString(12)),
			DateTimeOffset.Parse(reader.GetString(13)));
	}

	private static IReadOnlyList<string> ParsePermissions(string? json, string role)
	{
		try
		{
			string[]? values = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<string[]>(json);
			return WebPermissions.Normalize(values is { Length: > 0 } ? values : null, role);
		}
		catch (JsonException)
		{
			return WebPermissions.DefaultsForRole(role);
		}
	}

	private static string SerializePermissions(IReadOnlyList<string> permissions) => JsonSerializer.Serialize(permissions);

	private static async Task EnsureColumnAsync(SqliteConnection connection, string columnName, string definition, CancellationToken cancellationToken)
	{
		await using SqliteCommand inspect = connection.CreateCommand();
		inspect.CommandText = "PRAGMA table_info(web_users)";
		await using SqliteDataReader reader = await inspect.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}
		await reader.DisposeAsync();
		await using SqliteCommand alter = connection.CreateCommand();
		alter.CommandText = $"ALTER TABLE web_users ADD COLUMN {columnName} {definition}";
		await alter.ExecuteNonQueryAsync(cancellationToken);
	}

	private static void ValidateProfile(string userName, string displayName, string email, string role, bool validateUserName = true)
	{
		if (validateUserName && (string.IsNullOrWhiteSpace(userName) || userName.Trim().Length is < 3 or > 100))
		{
			throw new ArgumentException("Der Benutzername muss zwischen 3 und 100 Zeichen lang sein.", nameof(userName));
		}
		if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length is < 2 or > 120)
		{
			throw new ArgumentException("Der Anzeigename muss zwischen 2 und 120 Zeichen lang sein.", nameof(displayName));
		}
		try
		{
			_ = new MailAddress(email.Trim());
		}
		catch (FormatException)
		{
			throw new ArgumentException("Bitte eine gültige E-Mail-Adresse angeben.", nameof(email));
		}
		ValidateRole(role);
	}

	private static void ValidateRole(string role)
	{
		_ = WebPermissions.DefaultsForRole(role);
	}

	private static void ValidateCredentials(string userName, string password)
	{
		if (string.IsNullOrWhiteSpace(userName) || userName.Trim().Length is < 3 or > 100)
		{
			throw new ArgumentException("Der Benutzername muss zwischen 3 und 100 Zeichen lang sein.", nameof(userName));
		}
		ValidateNewPassword(password);
	}

	private static void ValidateNewPassword(string password)
	{
		if (string.IsNullOrEmpty(password) || password.Length < 14)
		{
			throw new ArgumentException("Das Kennwort muss mindestens 14 Zeichen lang sein.", nameof(password));
		}
		if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
		{
			throw new ArgumentException("Das Kennwort muss Groß- und Kleinbuchstaben sowie mindestens eine Zahl enthalten.", nameof(password));
		}
	}

	public static string GenerateTemporaryPassword()
	{
		const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
		const string lower = "abcdefghijkmnopqrstuvwxyz";
		const string digits = "23456789";
		const string symbols = "!%+-_";
		const string all = upper + lower + digits + symbols;
		char[] chars = new char[20];
		chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
		chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
		chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
		chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
		for (int index = 4; index < chars.Length; index++)
		{
			chars[index] = all[RandomNumberGenerator.GetInt32(all.Length)];
		}
		RandomNumberGenerator.Shuffle<char>(chars.AsSpan());
		return new string(chars);
	}

	private static string HashPassword(string password)
	{
		byte[] salt = RandomNumberGenerator.GetBytes(16);
		byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
		return $"PBKDF2-SHA256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
	}

	private static bool VerifyPassword(string password, string encoded)
	{
		try
		{
			string[] parts = encoded.Split('$');
			if (parts.Length != 4 || !string.Equals(parts[0], "PBKDF2-SHA256", StringComparison.Ordinal) || !int.TryParse(parts[1], out int iterations))
			{
				return false;
			}
			byte[] salt = Convert.FromBase64String(parts[2]);
			byte[] expected = Convert.FromBase64String(parts[3]);
			byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
			return CryptographicOperations.FixedTimeEquals(actual, expected);
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static async Task UpdateFailureAsync(SqliteConnection connection, Guid id, int failed, DateTimeOffset? lockedUntil, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE web_users SET failed_attempts=$failed,locked_until=$locked,updated_at=$now WHERE id=$id";
		command.Parameters.AddWithValue("$failed", failed);
		command.Parameters.AddWithValue("$locked", lockedUntil?.ToString("O") is { } value ? value : DBNull.Value);
		command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
		command.Parameters.AddWithValue("$id", id.ToString());
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task UpdateSuccessAsync(SqliteConnection connection, Guid id, DateTimeOffset loginAt, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE web_users SET failed_attempts=0,locked_until=NULL,last_login_at=$now,updated_at=$now WHERE id=$id";
		command.Parameters.AddWithValue("$now", loginAt.ToString("O"));
		command.Parameters.AddWithValue("$id", id.ToString());
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task AuditAsync(SqliteConnection connection, string userName, string action, string? remoteAddress, string detail, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "INSERT INTO web_auth_audit(occurred_at,user_name,action,remote_address,detail) VALUES($at,$name,$action,$remote,$detail)";
		command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
		command.Parameters.AddWithValue("$name", userName);
		command.Parameters.AddWithValue("$action", action);
		command.Parameters.AddWithValue("$remote", remoteAddress is null ? DBNull.Value : remoteAddress);
		command.Parameters.AddWithValue("$detail", detail);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private sealed record WebUserRow(WebUser User, string PasswordHash);
}
