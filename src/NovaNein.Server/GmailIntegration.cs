using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaNein.Server;

public sealed class GmailHistoryExpiredException : Exception
{
	public GmailHistoryExpiredException() : base("Die Gmail-History-ID ist abgelaufen.") { }
}

public sealed record GmailMessageEnvelope(
	string Id,
	string ThreadId,
	string HistoryId,
	string Subject,
	string Sender,
	DateTimeOffset ReceivedAt,
	JsonElement Payload);

public sealed record GmailAttachmentDescriptor(
	string AttachmentId,
	string PartId,
	string FileName,
	string MimeType,
	long Size,
	string? InlineData);

public interface IGmailApiClient
{
	bool IsConfigured { get; }
	Task<IReadOnlyList<string>> ListMessageIdsAsync(string query, CancellationToken cancellationToken = default);
	Task<(IReadOnlyList<string> MessageIds, string? LatestHistoryId)> ListHistoryAsync(string startHistoryId, CancellationToken cancellationToken = default);
	Task<GmailMessageEnvelope> GetMessageAsync(string messageId, CancellationToken cancellationToken = default);
	Task<byte[]> DownloadAttachmentAsync(string messageId, GmailAttachmentDescriptor attachment, CancellationToken cancellationToken = default);
	Task<IReadOnlyDictionary<string, string>> EnsureLabelsAsync(CancellationToken cancellationToken = default);
	Task ModifyLabelsAsync(string messageId, IEnumerable<string> addLabelIds, IEnumerable<string>? removeLabelIds = null, CancellationToken cancellationToken = default);
	Task<(string HistoryId, DateTimeOffset Expiration)> RenewWatchAsync(CancellationToken cancellationToken = default);
	Task<int> PullNotificationsAsync(CancellationToken cancellationToken = default);
}

public sealed class GmailCredentialProvider(IConfiguration configuration)
{
	public GmailCredentialSecret? Read()
	{
		var target = configuration["Gmail:CredentialTarget"] ?? "NovaNein/Gmail/invoices@example.invalid";
		if (WindowsCredentialManager.TryReadSecret(target, out _, out var secret))
			return Parse(secret);

		var clientId = configuration["Gmail:ClientId"];
		var clientSecret = configuration["Gmail:ClientSecret"];
		var refreshToken = configuration["Gmail:RefreshToken"];
		if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(refreshToken))
			return null;
		return new GmailCredentialSecret(clientId, clientSecret, refreshToken);
	}

	private static GmailCredentialSecret Parse(string json)
	{
		try
		{
			return JsonSerializer.Deserialize<GmailCredentialSecret>(json)
				?? throw new InvalidDataException("Der Gmail-Credential-Eintrag ist leer.");
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Der Gmail-Credential-Eintrag muss client_id, client_secret und refresh_token als JSON enthalten.", exception);
		}
	}
}

public sealed record PubSubServiceAccountCredential(
	string ProjectId,
	string PrivateKeyId,
	string PrivateKey,
	string ClientEmail,
	string TokenUri);

public sealed class PubSubServiceAccountCredentialProvider(IConfiguration configuration)
{
	public PubSubServiceAccountCredential? Read()
	{
		var configuredPath = configuration["Gmail:PubSubServiceAccountCredentialPath"];
		if (string.IsNullOrWhiteSpace(configuredPath)) return null;
		var path = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
		if (!Path.IsPathFullyQualified(path))
			throw new InvalidDataException("Gmail:PubSubServiceAccountCredentialPath muss ein absoluter Pfad sein.");
		if (!File.Exists(path))
			throw new FileNotFoundException("Die Pub/Sub-Dienstkonto-Schlüsseldatei fehlt.", path);

		try
		{
			using var stream = File.OpenRead(path);
			using var json = JsonDocument.Parse(stream);
			var root = json.RootElement;
			if (!string.Equals(root.GetProperty("type").GetString(), "service_account", StringComparison.Ordinal))
				throw new InvalidDataException("Die Pub/Sub-Credential-Datei ist kein Google-Dienstkontoschlüssel.");
			var credential = new PubSubServiceAccountCredential(
				root.GetProperty("project_id").GetString() ?? string.Empty,
				root.TryGetProperty("private_key_id", out var privateKeyId) ? privateKeyId.GetString() ?? string.Empty : string.Empty,
				root.GetProperty("private_key").GetString() ?? string.Empty,
				root.GetProperty("client_email").GetString() ?? string.Empty,
				root.GetProperty("token_uri").GetString() ?? string.Empty);
			Validate(credential);
			return credential;
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Die Pub/Sub-Dienstkonto-Schlüsseldatei enthält kein gültiges JSON.", exception);
		}
	}

	private void Validate(PubSubServiceAccountCredential credential)
	{
		var expectedProject = configuration["Gmail:PubSubProjectId"] ?? "example-project";
		var expectedEmail = configuration["Gmail:PubSubServiceAccountEmail"]
			?? "subscriber@example.invalid";
		if (!string.Equals(credential.ProjectId, expectedProject, StringComparison.Ordinal))
			throw new InvalidDataException("Der Pub/Sub-Dienstkontoschlüssel gehört zum falschen Google-Cloud-Projekt.");
		if (!string.Equals(credential.ClientEmail, expectedEmail, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("Der Pub/Sub-Dienstkontoschlüssel gehört zum falschen Dienstkonto.");
		if (!credential.PrivateKey.StartsWith("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal))
			throw new InvalidDataException("Der private Schlüssel des Pub/Sub-Dienstkontos fehlt.");
		if (!Uri.TryCreate(credential.TokenUri, UriKind.Absolute, out var tokenUri)
			|| !string.Equals(tokenUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(tokenUri.Host, "oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("Die Token-URL des Pub/Sub-Dienstkontos ist nicht zulässig.");
	}
}

public sealed class PubSubAccessTokenProvider(
	IHttpClientFactory clientFactory,
	PubSubServiceAccountCredentialProvider credentials)
{
	private readonly SemaphoreSlim _tokenLock = new(1, 1);
	private string? _accessToken;
	private DateTimeOffset _accessTokenExpiresAt;

	public bool IsConfigured => credentials.Read() != null;

	public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
	{
		if (_accessToken != null && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _accessToken;
		await _tokenLock.WaitAsync(cancellationToken);
		try
		{
			if (_accessToken != null && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _accessToken;
			var credential = credentials.Read()
				?? throw new InvalidOperationException("Das separate Pub/Sub-Dienstkonto ist nicht konfiguriert.");
			var now = DateTimeOffset.UtcNow;
			var header = new Dictionary<string, object>
			{
				["alg"] = "RS256",
				["typ"] = "JWT"
			};
			if (!string.IsNullOrWhiteSpace(credential.PrivateKeyId)) header["kid"] = credential.PrivateKeyId;
			var claims = new Dictionary<string, object>
			{
				["iss"] = credential.ClientEmail,
				["scope"] = "https://www.googleapis.com/auth/pubsub",
				["aud"] = credential.TokenUri,
				["iat"] = now.ToUnixTimeSeconds(),
				["exp"] = now.AddMinutes(55).ToUnixTimeSeconds()
			};
			var unsigned = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))
				+ "."
				+ Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
			using var rsa = RSA.Create();
			rsa.ImportFromPem(credential.PrivateKey);
			var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			var assertion = unsigned + "." + Base64Url(signature);
			var client = clientFactory.CreateClient("GoogleOAuth");
			using var response = await client.PostAsync(
				credential.TokenUri,
				new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
					["assertion"] = assertion
				}),
				cancellationToken);
			response.EnsureSuccessStatusCode();
			using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
			_accessToken = json.RootElement.GetProperty("access_token").GetString()
				?? throw new InvalidDataException("Google OAuth lieferte kein Pub/Sub-Access-Token.");
			var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
			_accessTokenExpiresAt = now.AddSeconds(expiresIn);
			return _accessToken;
		}
		finally
		{
			_tokenLock.Release();
		}
	}

	public void Invalidate()
	{
		_accessToken = null;
		_accessTokenExpiresAt = default;
	}

	private static string Base64Url(byte[] value)
		=> Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class GmailApiClient(
	IHttpClientFactory clientFactory,
	GmailCredentialProvider credentials,
	PubSubAccessTokenProvider pubSubTokens,
	IConfiguration configuration) : IGmailApiClient
{
	private static readonly string[] LabelNames =
	[
		"NovaNein/Importiert",
		"NovaNein/Prüfung",
		"NovaNein/Gebucht",
		"NovaNein/Fehler"
	];

	private readonly SemaphoreSlim _tokenLock = new(1, 1);
	private string? _accessToken;
	private DateTimeOffset _accessTokenExpiresAt;

	public bool IsConfigured => credentials.Read() != null;

	public async Task<IReadOnlyList<string>> ListMessageIdsAsync(string query, CancellationToken cancellationToken = default)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		string? pageToken = null;
		do
		{
			var uri = $"gmail/v1/users/me/messages?q={Uri.EscapeDataString(query)}&maxResults=100";
			if (!string.IsNullOrWhiteSpace(pageToken)) uri += "&pageToken=" + Uri.EscapeDataString(pageToken);
			using var response = await SendAsync(HttpMethod.Get, uri, null, cancellationToken);
			response.EnsureSuccessStatusCode();
			using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
			if (json.RootElement.TryGetProperty("messages", out var messages))
				foreach (var message in messages.EnumerateArray())
					if (message.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
						result.Add(id.GetString()!);
			pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
		}
		while (!string.IsNullOrWhiteSpace(pageToken));
		return result.ToArray();
	}

	public async Task<(IReadOnlyList<string> MessageIds, string? LatestHistoryId)> ListHistoryAsync(string startHistoryId, CancellationToken cancellationToken = default)
	{
		var ids = new HashSet<string>(StringComparer.Ordinal);
		string? pageToken = null;
		string? latest = null;
		do
		{
			var uri = $"gmail/v1/users/me/history?startHistoryId={Uri.EscapeDataString(startHistoryId)}&historyTypes=messageAdded&maxResults=100";
			if (!string.IsNullOrWhiteSpace(pageToken)) uri += "&pageToken=" + Uri.EscapeDataString(pageToken);
			using var response = await SendAsync(HttpMethod.Get, uri, null, cancellationToken);
			if (response.StatusCode == HttpStatusCode.NotFound) throw new GmailHistoryExpiredException();
			response.EnsureSuccessStatusCode();
			using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
			latest = json.RootElement.TryGetProperty("historyId", out var historyId) ? historyId.GetString() : latest;
			if (json.RootElement.TryGetProperty("history", out var history))
			{
				foreach (var entry in history.EnumerateArray())
				if (entry.TryGetProperty("messagesAdded", out var added))
					foreach (var messageAdded in added.EnumerateArray())
						if (messageAdded.TryGetProperty("message", out var message)
							&& message.TryGetProperty("id", out var id)
							&& !string.IsNullOrWhiteSpace(id.GetString()))
							ids.Add(id.GetString()!);
			}
			pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
		}
		while (!string.IsNullOrWhiteSpace(pageToken));
		return (ids.ToArray(), latest);
	}

	public async Task<GmailMessageEnvelope> GetMessageAsync(string messageId, CancellationToken cancellationToken = default)
	{
		using var response = await SendAsync(HttpMethod.Get, $"gmail/v1/users/me/messages/{Uri.EscapeDataString(messageId)}?format=full", null, cancellationToken);
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		var root = json.RootElement;
		var payload = root.GetProperty("payload").Clone();
		var internalDate = root.TryGetProperty("internalDate", out var date)
			&& long.TryParse(date.GetString(), out var milliseconds)
			? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
			: DateTimeOffset.UtcNow;
		return new GmailMessageEnvelope(
			root.GetProperty("id").GetString() ?? messageId,
			root.GetProperty("threadId").GetString() ?? string.Empty,
			root.TryGetProperty("historyId", out var history) ? history.GetString() ?? string.Empty : string.Empty,
			ReadHeader(payload, "Subject"),
			ReadHeader(payload, "From"),
			internalDate,
			payload);
	}

	public async Task<byte[]> DownloadAttachmentAsync(string messageId, GmailAttachmentDescriptor attachment, CancellationToken cancellationToken = default)
	{
		if (!string.IsNullOrWhiteSpace(attachment.InlineData))
			return DecodeBase64Url(attachment.InlineData);
		using var response = await SendAsync(
			HttpMethod.Get,
			$"gmail/v1/users/me/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachment.AttachmentId)}",
			null,
			cancellationToken);
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		return DecodeBase64Url(json.RootElement.GetProperty("data").GetString() ?? string.Empty);
	}

	public async Task<IReadOnlyDictionary<string, string>> EnsureLabelsAsync(CancellationToken cancellationToken = default)
	{
		using var list = await SendAsync(HttpMethod.Get, "gmail/v1/users/me/labels", null, cancellationToken);
		list.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await list.Content.ReadAsStreamAsync(cancellationToken));
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		if (json.RootElement.TryGetProperty("labels", out var labels))
			foreach (var label in labels.EnumerateArray())
			{
				var name = label.GetProperty("name").GetString();
				var id = label.GetProperty("id").GetString();
				if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) result[name] = id;
			}
		foreach (var name in LabelNames.Where(name => !result.ContainsKey(name)))
		{
			using var create = await SendAsync(HttpMethod.Post, "gmail/v1/users/me/labels", new
			{
				name,
				labelListVisibility = "labelShow",
				messageListVisibility = "show"
			}, cancellationToken);
			create.EnsureSuccessStatusCode();
			using var created = JsonDocument.Parse(await create.Content.ReadAsStreamAsync(cancellationToken));
			result[name] = created.RootElement.GetProperty("id").GetString()!;
		}
		return result;
	}

	public async Task ModifyLabelsAsync(string messageId, IEnumerable<string> addLabelIds, IEnumerable<string>? removeLabelIds = null, CancellationToken cancellationToken = default)
	{
		using var response = await SendAsync(
			HttpMethod.Post,
			$"gmail/v1/users/me/messages/{Uri.EscapeDataString(messageId)}/modify",
			new { addLabelIds = addLabelIds.Distinct().ToArray(), removeLabelIds = (removeLabelIds ?? []).Distinct().ToArray() },
			cancellationToken);
		response.EnsureSuccessStatusCode();
	}

	public async Task<(string HistoryId, DateTimeOffset Expiration)> RenewWatchAsync(CancellationToken cancellationToken = default)
	{
		var topic = configuration["Gmail:PubSubTopic"];
		if (string.IsNullOrWhiteSpace(topic))
			throw new InvalidOperationException("Gmail:PubSubTopic fehlt.");
		using var response = await SendAsync(HttpMethod.Post, "gmail/v1/users/me/watch", new
		{
			topicName = topic,
			labelFilterBehavior = "include",
			labelIds = new[] { "INBOX" }
		}, cancellationToken);
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		var expiration = long.Parse(json.RootElement.GetProperty("expiration").GetString()!);
		return (json.RootElement.GetProperty("historyId").GetString()!, DateTimeOffset.FromUnixTimeMilliseconds(expiration));
	}

	public async Task<int> PullNotificationsAsync(CancellationToken cancellationToken = default)
	{
		var subscription = configuration["Gmail:PubSubSubscription"];
		if (string.IsNullOrWhiteSpace(subscription)) return 0;
		var token = await pubSubTokens.GetAccessTokenAsync(cancellationToken);
		var client = clientFactory.CreateClient("GooglePubSub");
		var response = await SendPubSubAsync(
			client,
			$"v1/{subscription.TrimStart('/')}:pull",
			new { maxMessages = 100, returnImmediately = true },
			token,
			cancellationToken);
		using (response)
		{
			response.EnsureSuccessStatusCode();
			using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
			if (!json.RootElement.TryGetProperty("receivedMessages", out var messages) || messages.ValueKind != JsonValueKind.Array)
				return 0;
			var acknowledgements = messages.EnumerateArray()
				.Select(message => message.TryGetProperty("ackId", out var ack) ? ack.GetString() : null)
				.Where(id => !string.IsNullOrWhiteSpace(id))
				.ToArray();
			if (acknowledgements.Length == 0) return 0;
			using var acknowledged = await SendPubSubAsync(
				client,
				$"v1/{subscription.TrimStart('/')}:acknowledge",
				new { ackIds = acknowledgements },
				token,
				cancellationToken);
			acknowledged.EnsureSuccessStatusCode();
			return acknowledgements.Length;
		}
	}

	private async Task<HttpResponseMessage> SendPubSubAsync(
		HttpClient client,
		string relativeUri,
		object payload,
		string token,
		CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, relativeUri);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Content = JsonContent.Create(payload);
		var response = await client.SendAsync(request, cancellationToken);
		if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
		response.Dispose();
		pubSubTokens.Invalidate();
		token = await pubSubTokens.GetAccessTokenAsync(cancellationToken);
		using var retry = new HttpRequestMessage(HttpMethod.Post, relativeUri);
		retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		retry.Content = JsonContent.Create(payload);
		return await client.SendAsync(retry, cancellationToken);
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUri, object? payload, CancellationToken cancellationToken)
	{
		var token = await GetAccessTokenAsync(cancellationToken);
		var client = clientFactory.CreateClient("GmailApi");
		using var request = new HttpRequestMessage(method, relativeUri);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		if (payload != null) request.Content = JsonContent.Create(payload);
		var response = await client.SendAsync(request, cancellationToken);
		if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
		response.Dispose();
		_accessToken = null;
		token = await GetAccessTokenAsync(cancellationToken);
		using var retry = new HttpRequestMessage(method, relativeUri);
		retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		if (payload != null) retry.Content = JsonContent.Create(payload);
		return await client.SendAsync(retry, cancellationToken);
	}

	private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
	{
		if (_accessToken != null && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _accessToken;
		await _tokenLock.WaitAsync(cancellationToken);
		try
		{
			if (_accessToken != null && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _accessToken;
			var secret = credentials.Read() ?? throw new InvalidOperationException("Gmail OAuth ist nicht konfiguriert.");
			var client = clientFactory.CreateClient("GoogleOAuth");
			using var response = await client.PostAsync("token", new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["client_id"] = secret.ClientId,
				["client_secret"] = secret.ClientSecret,
				["refresh_token"] = secret.RefreshToken,
				["grant_type"] = "refresh_token"
			}), cancellationToken);
			response.EnsureSuccessStatusCode();
			using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
			_accessToken = json.RootElement.GetProperty("access_token").GetString()
				?? throw new InvalidDataException("Google OAuth lieferte kein Access-Token.");
			var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
			_accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
			return _accessToken;
		}
		finally
		{
			_tokenLock.Release();
		}
	}

	private static string ReadHeader(JsonElement payload, string name)
	{
		if (!payload.TryGetProperty("headers", out var headers)) return string.Empty;
		foreach (var header in headers.EnumerateArray())
			if (string.Equals(header.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
				return header.GetProperty("value").GetString() ?? string.Empty;
		return string.Empty;
	}

	private static byte[] DecodeBase64Url(string value)
	{
		var normalized = value.Replace('-', '+').Replace('_', '/');
		normalized += new string('=', (4 - normalized.Length % 4) % 4);
		return Convert.FromBase64String(normalized);
	}
}

public sealed class GmailIngestionService(
	IGmailApiClient gmail,
	AutomaticBookingStore store,
	PdfUploadStore pdfStore,
	InvoiceProposalService proposalService,
	IConfiguration configuration,
	ILogger<GmailIngestionService> logger)
{
	public const string RequiredMailbox = "invoices@example.invalid";

	public async Task<GmailSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
	{
		var state = await store.GetGmailStateAsync(RequiredMailbox, cancellationToken);
		var counts = await store.CountsAsync(cancellationToken);
		return new GmailSyncStatus(
			configuration.GetValue("Gmail:Enabled", false),
			gmail.IsConfigured,
			RequiredMailbox,
			state.HistoryId,
			state.WatchExpiration,
			state.LastSyncAt,
			state.LastSuccessfulSyncAt,
			state.LastError,
			counts.OpenProposals,
			counts.FailedMessages,
			counts.OrphanAttachments);
	}

	public async Task<GmailSyncStatus> SyncAsync(CancellationToken cancellationToken = default)
	{
		if (!configuration.GetValue("Gmail:Enabled", false)) return await GetStatusAsync(cancellationToken);
		var mailbox = configuration["Gmail:Mailbox"] ?? RequiredMailbox;
		if (!string.Equals(mailbox, RequiredMailbox, StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException($"NovaNein darf nur das festgelegte Postfach {RequiredMailbox} anbinden.");
		if (!gmail.IsConfigured) throw new InvalidOperationException("Gmail OAuth ist nicht konfiguriert.");

		var state = await store.GetGmailStateAsync(mailbox, cancellationToken);
		string? latestHistory = state.HistoryId;
		var expiration = state.WatchExpiration;
		try
		{
			await gmail.PullNotificationsAsync(cancellationToken);
			IReadOnlyList<string> messageIds;
			if (!string.IsNullOrWhiteSpace(state.HistoryId))
			{
				try
				{
					var history = await gmail.ListHistoryAsync(state.HistoryId, cancellationToken);
					messageIds = history.MessageIds;
					latestHistory = history.LatestHistoryId ?? latestHistory;
				}
				catch (GmailHistoryExpiredException)
				{
					messageIds = await FullSyncIdsAsync(cancellationToken);
					latestHistory = null;
				}
			}
			else
			{
				if (configuration.GetValue("Gmail:BackfillOnFirstSync", false))
				{
					messageIds = await FullSyncIdsAsync(cancellationToken);
				}
				else
				{
					var watch = await gmail.RenewWatchAsync(cancellationToken);
					latestHistory = watch.HistoryId;
					expiration = watch.Expiration;
					messageIds = [];
				}
			}

			var labels = await gmail.EnsureLabelsAsync(cancellationToken);
			foreach (var messageId in messageIds)
				await ProcessMessageAsync(messageId, labels, cancellationToken);

			if (!string.IsNullOrWhiteSpace(configuration["Gmail:PubSubTopic"])
				&& (!expiration.HasValue || expiration < DateTimeOffset.UtcNow.AddDays(2)))
			{
				var watch = await gmail.RenewWatchAsync(cancellationToken);
				latestHistory = watch.HistoryId;
				expiration = watch.Expiration;
			}
			await store.SaveGmailStateAsync(mailbox, latestHistory, expiration, true, null, cancellationToken);
		}
		catch (Exception exception)
		{
			await store.SaveGmailStateAsync(mailbox, latestHistory, state.WatchExpiration, false, exception.Message, cancellationToken);
			logger.LogError(exception, "Gmail-Synchronisierung für {Mailbox} ist fehlgeschlagen.", mailbox);
			throw;
		}
		return await GetStatusAsync(cancellationToken);
	}

	private Task<IReadOnlyList<string>> FullSyncIdsAsync(CancellationToken cancellationToken)
		=> gmail.ListMessageIdsAsync("has:attachment -label:\"NovaNein/Gebucht\" -label:\"NovaNein/Fehler\"", cancellationToken);

	public async Task<MailSourceRecord> ImportLatestInvoiceAsync(CancellationToken cancellationToken = default)
	{
		if (!gmail.IsConfigured) throw new InvalidOperationException("Gmail OAuth ist nicht konfiguriert.");
		var labels = await gmail.EnsureLabelsAsync(cancellationToken);
		var messageIds = await gmail.ListMessageIdsAsync("in:anywhere has:attachment filename:pdf -in:sent", cancellationToken);
		foreach (var messageId in messageIds)
		{
			var existing = await store.GetMailByMessageIdAsync(messageId, cancellationToken);
			if (existing != null)
			{
				var existingProposals = (await store.ListProposalsAsync(ct: cancellationToken))
					.Where(proposal => proposal.MailSourceId == existing.Id)
					.ToArray();
				if (existingProposals.Length > 0)
				{
					foreach (var proposal in existingProposals.Where(proposal => proposal.Status is
						MailSourceStatuses.ProposalReady or
						MailSourceStatuses.NeedsReview or
						MailSourceStatuses.Blocked or
						MailSourceStatuses.Failed))
						await proposalService.RecalculateAsync(proposal.Id, cancellationToken);
					return await store.GetMailAsync(existing.Id, cancellationToken) ?? existing;
				}
				var readyAttachments = existing.Attachments?
					.Where(attachment => attachment.Status == "Ready")
					.ToArray() ?? [];
				if (readyAttachments.Length > 0)
				{
					foreach (var attachment in readyAttachments)
						await proposalService.CreateOrRecalculateAsync(existing, attachment, null, cancellationToken);
					await gmail.ModifyLabelsAsync(
						messageId,
						[labels["NovaNein/Importiert"], labels["NovaNein/Prüfung"]],
						[labels["NovaNein/Fehler"]],
						cancellationToken);
					return await store.GetMailAsync(existing.Id, cancellationToken) ?? existing;
				}
				if (!await store.DeleteUnprocessedMailForRetryAsync(existing.Id, cancellationToken)) return existing;
			}
			var message = await gmail.GetMessageAsync(messageId, cancellationToken);
			if (!ParseAttachments(message.Payload).Any(IsPdf)) continue;
			var imported = await ProcessMessageAsync(messageId, labels, cancellationToken);
			if (imported != null) return imported;
		}
		throw new InvalidOperationException("Keine empfangene Gmail-Nachricht mit PDF-Anhang gefunden.");
	}

	private static bool IsPdf(GmailAttachmentDescriptor attachment)
		=> string.Equals(Path.GetExtension(attachment.FileName), ".pdf", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(attachment.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);

	private async Task<MailSourceRecord?> ProcessMessageAsync(string messageId, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
	{
		var existing = await store.GetMailByMessageIdAsync(messageId, cancellationToken);
		if (existing != null) return existing;
		var message = await gmail.GetMessageAsync(messageId, cancellationToken);
		var descriptors = ParseAttachments(message.Payload);
		var attachments = new List<MailAttachmentRecord>();
		foreach (var descriptor in descriptors)
		{
			var data = await gmail.DownloadAttachmentAsync(message.Id, descriptor, cancellationToken);
			var sha = Convert.ToHexString(SHA256.HashData(data));
			if (await store.HasAttachmentHashAsync(sha, cancellationToken)) continue;
			var extension = Path.GetExtension(descriptor.FileName).ToLowerInvariant();
			if (IsPdfContent(descriptor.FileName, descriptor.MimeType, data))
			{
				await using var stream = new MemoryStream(data, writable: false);
				var stored = await pdfStore.StoreAsync(descriptor.FileName, data.LongLength, stream, cancellationToken);
				attachments.Add(new MailAttachmentRecord(Guid.NewGuid(), Guid.Empty, descriptor.AttachmentId, descriptor.FileName, descriptor.MimeType, data.LongLength, stored.Sha256, stored.Path, "Ready", null));
			}
			else if (extension is ".xml" or ".xrechnung" || descriptor.MimeType.Contains("xml", StringComparison.OrdinalIgnoreCase))
			{
				var path = await SaveBinaryAsync(configuration["Storage:MailRoot"] ?? "data/mail", sha, extension.Length > 0 ? extension : ".xml", data, cancellationToken);
				attachments.Add(new MailAttachmentRecord(Guid.NewGuid(), Guid.Empty, descriptor.AttachmentId, descriptor.FileName, descriptor.MimeType, data.LongLength, sha, path, "StructuredNeedsReview", "Strukturierte E-Rechnung wird gespeichert, aber im ersten Automatikumfang nicht gebucht."));
			}
			else
			{
				var path = await SaveBinaryAsync(configuration["Storage:QuarantineRoot"] ?? "data/quarantine", sha, extension.Length > 0 ? extension : ".bin", data, cancellationToken);
				attachments.Add(new MailAttachmentRecord(Guid.NewGuid(), Guid.Empty, descriptor.AttachmentId, descriptor.FileName, descriptor.MimeType, data.LongLength, sha, path, "Quarantined", "Dateityp ist für automatische Buchung nicht zugelassen."));
			}
		}
		if (attachments.Count == 0) return null;

		var mail = await store.CreateMailAsync(
			RequiredMailbox,
			message.Id,
			message.ThreadId,
			message.HistoryId,
			message.Subject,
			message.Sender,
			message.ReceivedAt,
			attachments,
			cancellationToken);
		try
		{
			var pdfs = mail.Attachments?.Where(attachment => attachment.Status == "Ready").ToArray() ?? [];
			foreach (var pdf in pdfs) await proposalService.CreateOrRecalculateAsync(mail, pdf, null, cancellationToken);
			var add = new List<string> { labels["NovaNein/Importiert"] };
			if (pdfs.Length > 0) add.Add(labels["NovaNein/Prüfung"]);
			await gmail.ModifyLabelsAsync(message.Id, add, cancellationToken: cancellationToken);
		}
		catch (Exception exception)
		{
			await store.SetMailStatusAsync(mail.Id, MailSourceStatuses.Failed, exception.Message, "gmail-worker", cancellationToken);
			await gmail.ModifyLabelsAsync(message.Id, [labels["NovaNein/Fehler"]], cancellationToken: cancellationToken);
			throw;
		}
		return await store.GetMailAsync(mail.Id, cancellationToken);
	}

	public static bool IsPdfContent(string fileName, string mimeType, ReadOnlySpan<byte> data)
	{
		var declaredAsPdf = string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
		return declaredAsPdf
			&& data.Length >= 5
			&& data[0] == (byte)'%'
			&& data[1] == (byte)'P'
			&& data[2] == (byte)'D'
			&& data[3] == (byte)'F'
			&& data[4] == (byte)'-';
	}

	public static IReadOnlyList<GmailAttachmentDescriptor> ParseAttachments(JsonElement payload)
		=> ReadAttachments(payload).ToArray();

	private static IEnumerable<GmailAttachmentDescriptor> ReadAttachments(JsonElement part)
	{
		var fileName = part.TryGetProperty("filename", out var name) ? name.GetString() ?? string.Empty : string.Empty;
		var mimeType = part.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? "application/octet-stream" : "application/octet-stream";
		var partId = part.TryGetProperty("partId", out var id) ? id.GetString() ?? string.Empty : string.Empty;
		if (!string.IsNullOrWhiteSpace(fileName) && part.TryGetProperty("body", out var body))
		{
			var attachmentId = body.TryGetProperty("attachmentId", out var attachment) ? attachment.GetString() : null;
			var inline = body.TryGetProperty("data", out var data) ? data.GetString() : null;
			var size = body.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0L;
			if (!string.IsNullOrWhiteSpace(attachmentId) || !string.IsNullOrWhiteSpace(inline))
				yield return new GmailAttachmentDescriptor(attachmentId ?? "inline:" + partId, partId, fileName, mimeType, size, inline);
		}
		if (part.TryGetProperty("parts", out var parts))
			foreach (var child in parts.EnumerateArray())
				foreach (var attachment in ReadAttachments(child))
					yield return attachment;
	}

	private static async Task<string> SaveBinaryAsync(string root, string sha, string extension, byte[] data, CancellationToken cancellationToken)
	{
		var fullRoot = Path.GetFullPath(root);
		Directory.CreateDirectory(fullRoot);
		var path = Path.Combine(fullRoot, sha + extension);
		if (!File.Exists(path)) await File.WriteAllBytesAsync(path, data, cancellationToken);
		return path;
	}
}

public sealed class GmailLatestInvoiceOneShotWorker(
	GmailIngestionService service,
	AutomaticBookingStore store,
	IConfiguration configuration,
	IHostApplicationLifetime lifetime,
	ILogger<GmailLatestInvoiceOneShotWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			var mail = await service.ImportLatestInvoiceAsync(stoppingToken);
			var proposals = (await store.ListProposalsAsync(ct: stoppingToken))
				.Where(proposal => proposal.MailSourceId == mail.Id)
				.Select(proposal => new
				{
					proposal.Id,
					proposal.Version,
					proposal.Status,
					proposal.Signal,
					proposal.DocumentType,
					proposal.InvoiceNumber,
					proposal.SupplierName,
					proposal.SupplierCode,
					proposal.InvoiceDate,
					proposal.NetAmount,
					proposal.TaxAmount,
					proposal.GrossAmount,
					proposal.Currency,
					proposal.SuggestionReason,
					proposal.Findings
				})
				.ToArray();
			var databasePath = Path.GetFullPath(configuration["Storage:DatabasePath"] ?? "data/novanein.db");
			var outputDirectory = Path.Combine(Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory, "logs");
			Directory.CreateDirectory(outputDirectory);
			var outputPath = Path.Combine(outputDirectory, "gmail-import-latest-result.json");
			await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
			{
				mail.Id,
				mail.GmailMessageId,
				mail.GmailThreadId,
				mail.Subject,
				mail.Sender,
				mail.ReceivedAt,
				mail.Status,
				Attachments = mail.Attachments?.Select(attachment => new
				{
					attachment.Id,
					attachment.FileName,
					attachment.MimeType,
					attachment.Size,
					attachment.Sha256,
					attachment.Status,
					attachment.Error
				}),
				Proposals = proposals
			}, new JsonSerializerOptions { WriteIndented = true }), stoppingToken);
			logger.LogInformation("Die letzte Gmail-Rechnung {MessageId} wurde gezielt importiert.", mail.GmailMessageId);
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Der gezielte Import der letzten Gmail-Rechnung ist fehlgeschlagen.");
			Environment.ExitCode = 1;
		}
		finally
		{
			lifetime.StopApplication();
		}
	}
}

public sealed class GmailIngestionWorker(
	GmailIngestionService service,
	IConfiguration configuration,
	ILogger<GmailIngestionWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				if (configuration.GetValue("Gmail:Enabled", false)) await service.SyncAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
			catch (Exception exception)
			{
				logger.LogError(exception, "Der Gmail-Ingestion-Lauf ist fehlgeschlagen.");
			}
			await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Gmail:PollingIntervalSeconds", 60), 30, 3600)), stoppingToken);
		}
	}
}
