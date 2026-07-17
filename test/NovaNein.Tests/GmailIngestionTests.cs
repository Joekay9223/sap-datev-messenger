using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class GmailIngestionTests
{
	[Fact]
	public void First_sync_does_not_backfill_historical_mail_by_default()
	{
		var configuration = new ConfigurationBuilder()
			.AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/NovaNein.Server/appsettings.json")))
			.Build();

		Assert.False(configuration.GetValue("Gmail:BackfillOnFirstSync", true));
	}

	[Fact]
	public void Reads_nested_mime_attachments_without_treating_body_text_as_an_attachment()
	{
		using var json = JsonDocument.Parse("""
			{
			  "mimeType":"multipart/mixed",
			  "parts":[
			    {"partId":"0","filename":"","mimeType":"text/plain","body":{"data":"SGFsbG8=","size":5}},
			    {"partId":"1","filename":"rechnung.pdf","mimeType":"application/pdf","body":{"attachmentId":"att-1","size":1234}},
			    {"partId":"2","filename":"","mimeType":"multipart/alternative","body":{"size":0},"parts":[
			      {"partId":"2.1","filename":"rechnung.xml","mimeType":"application/xml","body":{"data":"PGZha3R1cmE-Lw==","size":10}}
			    ]}
			  ]
			}
			""");

		var attachments = GmailIngestionService.ParseAttachments(json.RootElement);

		Assert.Equal(2, attachments.Count);
		Assert.Contains(attachments, item => item.FileName == "rechnung.pdf" && item.AttachmentId == "att-1");
		Assert.Contains(attachments, item => item.FileName == "rechnung.xml" && item.InlineData != null);
	}

	[Fact]
	public void Accepts_real_pdf_with_uppercase_extension_and_octet_stream_mime_type()
	{
		var data = Encoding.ASCII.GetBytes("%PDF-1.7\ninvoice");

		Assert.True(GmailIngestionService.IsPdfContent("26070259.PDF", "application/octet-stream", data));
		Assert.False(GmailIngestionService.IsPdfContent("26070259.PDF", "application/octet-stream", Encoding.ASCII.GetBytes("not a pdf")));
	}

	[Fact]
	public async Task PubSub_service_account_creates_a_scoped_signed_jwt_and_caches_the_token()
	{
		var directory = Directory.CreateTempSubdirectory("novanein-pubsub-");
		var path = Path.Combine(directory.FullName, "service-account.json");
		using var rsa = RSA.Create(2048);
		var privateKey = rsa.ExportPkcs8PrivateKeyPem();
		await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
		{
			type = "service_account",
			project_id = "example-project",
			private_key_id = "test-key",
			private_key = privateKey,
			client_email = "subscriber@example.invalid",
			token_uri = "https://oauth2.googleapis.com/token"
		}));
		try
		{
			string? assertion = null;
			var handler = new DelegateHandler(async request =>
			{
				var form = await request.Content!.ReadAsStringAsync();
				var values = form.Split('&')
					.Select(item => item.Split('=', 2))
					.ToDictionary(item => Uri.UnescapeDataString(item[0]), item => Uri.UnescapeDataString(item[1].Replace('+', ' ')));
				assertion = values["assertion"];
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent("""{"access_token":"pubsub-token","expires_in":3600}""", Encoding.UTF8, "application/json")
				};
			});
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Gmail:PubSubServiceAccountCredentialPath"] = path
			}).Build();
			var provider = new PubSubAccessTokenProvider(
				new StubHttpClientFactory(handler),
				new PubSubServiceAccountCredentialProvider(configuration));

			Assert.Equal("pubsub-token", await provider.GetAccessTokenAsync());
			Assert.Equal("pubsub-token", await provider.GetAccessTokenAsync());
			Assert.Equal(1, handler.RequestCount);
			Assert.NotNull(assertion);

			var parts = assertion!.Split('.');
			Assert.Equal(3, parts.Length);
			using var claims = JsonDocument.Parse(DecodeBase64Url(parts[1]));
			Assert.Equal(
				"subscriber@example.invalid",
				claims.RootElement.GetProperty("iss").GetString());
			Assert.Equal("https://www.googleapis.com/auth/pubsub", claims.RootElement.GetProperty("scope").GetString());
			Assert.Equal("https://oauth2.googleapis.com/token", claims.RootElement.GetProperty("aud").GetString());
			Assert.True(rsa.VerifyData(
				Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
				DecodeBase64Url(parts[2]),
				HashAlgorithmName.SHA256,
				RSASignaturePadding.Pkcs1));
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	private static byte[] DecodeBase64Url(string value)
	{
		var normalized = value.Replace('-', '+').Replace('_', '/');
		normalized += new string('=', (4 - normalized.Length % 4) % 4);
		return Convert.FromBase64String(normalized);
	}

	private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
	}

	private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
	{
		public int RequestCount { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestCount++;
			return await handler(request);
		}
	}
}
