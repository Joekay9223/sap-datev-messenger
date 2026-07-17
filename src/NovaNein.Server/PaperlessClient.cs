using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class PaperlessClient(HttpClient httpClient, IConfiguration configuration, IPaperlessTokenProvider tokenProvider)
{
	public async Task<PaperlessPage> ListAsync(int page = 1, int pageSize = 50, string? query = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		ValidateEndpoint();
		string recipient = configuration["Integrations:Paperless:Recipient"]?.Trim();
		List<string> parameters = new List<string>
		{
			$"page={Math.Max(page, 1)}",
			$"page_size={Math.Clamp(pageSize, 1, 100)}"
		};
		if (!string.IsNullOrWhiteSpace(query))
		{
			parameters.Add("query=" + Uri.EscapeDataString(query.Trim()));
		}
		if (!string.IsNullOrWhiteSpace(recipient))
		{
			parameters.Add("correspondent__name__icontains=" + Uri.EscapeDataString(recipient));
		}
		using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "api/documents/?" + string.Join('&', parameters), cancellationToken);
		await EnsureSuccessAsync(response);
		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		return ReadPage(json.RootElement);
	}

	public async Task<PaperlessDocument> GetAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (id <= 0)
		{
			throw new ArgumentOutOfRangeException("id");
		}
		ValidateEndpoint();
		using HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"api/documents/{id}/", cancellationToken);
		await EnsureSuccessAsync(response);
		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		return ReadDocument(json.RootElement);
	}

	public async Task<byte[]> DownloadAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (id <= 0)
		{
			throw new ArgumentOutOfRangeException("id");
		}
		ValidateEndpoint();
		using HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"api/documents/{id}/download/", cancellationToken);
		await EnsureSuccessAsync(response);
		byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
		if (content.Length < 5 || !content.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
		{
			throw new InvalidDataException("Paperless hat keine gültige PDF-Signatur geliefert.");
		}
		return content;
	}

	public async Task<IReadOnlyList<PaperlessMatchCandidate>> FindCandidatesAsync(SapDocumentSnapshot snapshot, CancellationToken cancellationToken = default(CancellationToken))
	{
		return (from candidate in (await ListAsync(1, 50, snapshot.InvoiceNumber, cancellationToken)).Results.Select(delegate(PaperlessDocument document)
			{
				List<string> list = new List<string>();
				int num = 0;
				if (!string.IsNullOrWhiteSpace(snapshot.InvoiceNumber) && document.Title.Contains(snapshot.InvoiceNumber, StringComparison.OrdinalIgnoreCase))
				{
					num += 3;
					list.Add("Rechnungsnummer im Paperless-Titel");
				}
				if (!string.IsNullOrWhiteSpace(snapshot.BusinessPartnerName) && document.Correspondent.Contains(snapshot.BusinessPartnerName, StringComparison.OrdinalIgnoreCase))
				{
					num += 3;
					list.Add("Geschäftspartner/Empfänger passt");
				}
				DateOnly? documentDate = document.DocumentDate;
				if (documentDate.HasValue && Math.Abs(documentDate.GetValueOrDefault().DayNumber - snapshot.DocumentDate.DayNumber) <= 3)
				{
					num += 2;
					list.Add("Belegdatum innerhalb von ±3 Tagen");
				}
				decimal? amount = document.Amount;
				if (amount.HasValue)
				{
					decimal valueOrDefault = amount.GetValueOrDefault();
					if (Math.Abs(valueOrDefault - snapshot.GrossAmount) <= 0.01m)
					{
						num += 3;
						list.Add("Betrag stimmt centgenau");
					}
				}
				if (!string.IsNullOrWhiteSpace(document.Currency) && string.Equals(document.Currency, snapshot.Currency, StringComparison.OrdinalIgnoreCase))
				{
					num++;
					list.Add("Währung passt");
				}
				return new PaperlessMatchCandidate(document, num, list);
			})
			where candidate.Score >= 6
			orderby candidate.Score descending, candidate.Document.Id
			select candidate).ToArray();
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
	{
		if (!tokenProvider.TryGetToken(out string token) || string.IsNullOrWhiteSpace(token))
		{
			throw new PaperlessAuthenticationException("Paperless-Token fehlt im Windows Credential Manager. Erwartet wird der Eintrag Integrations/Paperless.");
		}
		using HttpRequestMessage request = new HttpRequestMessage(method, path);
		request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
		return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
	}

	private void ValidateEndpoint()
	{
		string configured = configuration["Integrations:Paperless:BaseUrl"];
		if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri uri) || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
		{
			throw new PaperlessConfigurationException("Integrations:Paperless:BaseUrl ist ungültig.");
		}
		if (uri.Scheme == Uri.UriSchemeHttp && !IsPrivateHttpHost(uri.Host))
		{
			throw new PaperlessConfigurationException("Paperless-Token wird ausschließlich über das interne Netz oder HTTPS übertragen. Der öffentliche HTTP-Port 8100 ist gesperrt.");
		}
	}

	internal static bool IsPrivateHttpHost(string host)
	{
		if (IPAddress.TryParse(host, out IPAddress ip))
		{
			if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
			if (IPAddress.IsLoopback(ip)) return true;
			byte[] bytes = ip.GetAddressBytes();
			return bytes.Length == 4 &&
				(bytes[0] == 10 ||
				 (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
				 (bytes[0] == 192 && bytes[1] == 168));
		}
		return false;
	}

	private static Task EnsureSuccessAsync(HttpResponseMessage response)
	{
		if (response.IsSuccessStatusCode)
		{
			return Task.CompletedTask;
		}
		HttpStatusCode status = response.StatusCode;
		response.Dispose();
		if ((status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden) ? true : false)
		{
			throw new PaperlessAuthenticationException($"Paperless hat den Zugriff abgelehnt (HTTP {status}). Token und Benutzerkontext prüfen.");
		}
		throw new HttpRequestException($"Paperless antwortete mit HTTP {status}.");
	}

	private static PaperlessPage ReadPage(JsonElement root)
	{
		JsonElement array;
		PaperlessDocument[] results = ((root.TryGetProperty("results", out array) && array.ValueKind == JsonValueKind.Array) ? array.EnumerateArray().Select(ReadDocument).ToArray() : Array.Empty<PaperlessDocument>());
		JsonElement count;
		JsonElement next;
		JsonElement previous;
		return new PaperlessPage(results, root.TryGetProperty("count", out count) ? count.GetInt32() : results.Length, (root.TryGetProperty("next", out next) && next.ValueKind == JsonValueKind.String) ? next.GetString() : null, (root.TryGetProperty("previous", out previous) && previous.ValueKind == JsonValueKind.String) ? previous.GetString() : null);
	}

	private static PaperlessDocument ReadDocument(JsonElement root)
	{
		JsonElement tagArray;
		string[] tags = ((root.TryGetProperty("tags", out tagArray) && tagArray.ValueKind == JsonValueKind.Array) ? (from x in tagArray.EnumerateArray().Select(delegate(JsonElement x)
			{
				object obj;
				if (x.ValueKind != JsonValueKind.String)
				{
					if (!x.TryGetProperty("name", out var value))
					{
						return string.Empty;
					}
					obj = value.GetString();
					if (obj == null)
					{
						return string.Empty;
					}
				}
				else
				{
					obj = x.GetString() ?? string.Empty;
				}
				return (string)obj;
			})
			where x.Length > 0
			select x).ToArray() : Array.Empty<string>());
		return new PaperlessDocument(root.GetProperty("id").GetInt32(), StringProperty(root, "title"), StringProperty(root, "correspondent"), tags, DateProperty(root, "created"), DateProperty(root, "document_date"), NullableString(root, "archive_serial_number"), NullableString(root, "original_file_name"), DecimalProperty(root, "custom_fields", "amount"), NullableString(root, "currency"));
	}

	private static string StringProperty(JsonElement root, string name)
	{
		object obj;
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			if (!root.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("name", out var nested))
			{
				return string.Empty;
			}
			obj = nested.GetString();
			if (obj == null)
			{
				return string.Empty;
			}
		}
		else
		{
			obj = value.GetString() ?? string.Empty;
		}
		return (string)obj;
	}

	private static string? NullableString(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}

	private static DateOnly? DateProperty(JsonElement root, string name)
	{
		if (!DateOnly.TryParse(NullableString(root, name), out var date))
		{
			return null;
		}
		return date;
	}

	private static decimal? DecimalProperty(JsonElement root, string objectName, string key)
	{
		if (!root.TryGetProperty(objectName, out var fields) || fields.ValueKind != JsonValueKind.Array)
		{
			return null;
		}
		foreach (JsonElement field in fields.EnumerateArray())
		{
			if (field.TryGetProperty("field", out var fieldName) && string.Equals(fieldName.GetString(), key, StringComparison.OrdinalIgnoreCase) && field.TryGetProperty("value", out var value) && decimal.TryParse(value.ToString(), out var result))
			{
				return result;
			}
		}
		return null;
	}
}
