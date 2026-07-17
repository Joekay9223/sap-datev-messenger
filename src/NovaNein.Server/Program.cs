using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaNein.Datev;
using NovaNein.Domain;
using NovaNein.Server;

public class Program
{
	public static async Task Main(string[] args)
	{
		// Der Windows Service Control Manager startet Dienste standardmäßig aus
		// C:\Windows\System32. Alle bewusst relativen Daten- und Paketpfade müssen
		// dagegen stabil relativ zum installierten NovaNein-Publish aufgelöst werden.
		Directory.SetCurrentDirectory(AppContext.BaseDirectory);
		WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			Args = args,
			ContentRootPath = AppContext.BaseDirectory
		});
		if (args.Length == 2 && string.Equals(args[0], "--bootstrap-web-admin", StringComparison.OrdinalIgnoreCase))
		{
			string password = Environment.GetEnvironmentVariable("NOVANEIN_BOOTSTRAP_PASSWORD");
			if (string.IsNullOrEmpty(password))
			{
				Console.Write("Kennwort für den NovaNein-Web-Admin: ");
				password = ReadConsolePassword();
				Console.WriteLine();
			}
			WebIdentityStore identities = new(builder.Configuration);
			await identities.InitializeAsync();
			Console.WriteLine("Web-Administrator '" + (await identities.CreateOrReplaceAdminAsync(args[1], password)).UserName + "' wurde eingerichtet.");
			return;
		}
		if (args.Length == 5 && string.Equals(args[0], "--provision-web-user", StringComparison.OrdinalIgnoreCase))
		{
			string password = Environment.GetEnvironmentVariable("NOVANEIN_BOOTSTRAP_PASSWORD");
			if (string.IsNullOrEmpty(password))
			{
				throw new InvalidOperationException("NOVANEIN_BOOTSTRAP_PASSWORD muss für die sichere Benutzeranlage gesetzt sein.");
			}
			WebIdentityStore identities = new(builder.Configuration);
			await identities.InitializeAsync();
			WebUserRequest request = new(args[1], args[2], args[3], args[4], Password: password, MustChangePassword: true);
			WebUserProvisioningResult result = await identities.CreateAsync(request, "cli-provisioning");
			Console.WriteLine($"Web-Benutzer '{result.User.UserName}' mit Rolle {result.User.Role} wurde eingerichtet.");
			return;
		}
		WindowsServiceLifetimeHostBuilderExtensions.UseWindowsService((IHostBuilder)builder.Host);
		builder.Services.Configure(delegate(FormOptions options)
		{
			options.MultipartBodyLengthLimit = 53477376L;
		});
		builder.Services.AddSignalR();
		builder.Services.AddSingleton<DatevBookingCsvParser>();
		builder.Services.AddSingleton<AccountingImportStore>();
		builder.Services.AddSingleton<DatevMappingStore>();
		builder.Services.AddSingleton<ReconciliationService>();
		builder.Services.AddSingleton<CockpitStatusNotifier>();
		builder.Services.AddSingleton<DocumentStore>();
		builder.Services.AddSingleton<WorkstationRegistry>();
		builder.Services.AddSingleton<ReminderStore>();
		builder.Services.AddSingleton<TransferEvidenceStore>();
		builder.Services.AddSingleton<DatevTransferRequestStore>();
		builder.Services.AddSingleton<WebIdentityStore>();
		builder.Services.AddSingleton<AiReviewAuditStore>();
		builder.Services.AddSingleton<PdfInboxStore>();
		builder.Services.AddSingleton<WorkItemIgnoreStore>();
		builder.Services.AddSingleton<WorkItemService>();
		builder.Services.AddSingleton<BusinessStatisticsService>();
		builder.Services.AddSingleton<AutomaticBookingStore>();
		builder.Services.AddSingleton<GmailCredentialProvider>();
		builder.Services.AddSingleton<PubSubServiceAccountCredentialProvider>();
		builder.Services.AddSingleton<PubSubAccessTokenProvider>();
		builder.Services.AddSingleton<IGmailApiClient, GmailApiClient>();
		builder.Services.AddSingleton<InvoiceProposalService>();
		builder.Services.AddSingleton<GmailIngestionService>();
		builder.Services.AddSingleton<PdfInboxService>();
		builder.Services.AddSingleton<IPaperlessTokenProvider, CredentialManagerPaperlessTokenProvider>();
		builder.Services.AddHttpClient<PaperlessClient>(delegate(IServiceProvider services, HttpClient client)
		{
			string text = services.GetRequiredService<IConfiguration>()["Integrations:Paperless:BaseUrl"] ?? "http://127.0.0.1:8000";
			client.BaseAddress = new Uri(text.EndsWith('/') ? text : (text + "/"));
			client.Timeout = TimeSpan.FromSeconds(20.0);
		});
		builder.Services.AddSingleton<DatevPackageGenerator>();
		builder.Services.AddSingleton<DatevPackageProcessor>();
		builder.Services.AddSingleton<DocumentJobQueue>();
		builder.Services.AddSingleton<IncomingDocumentIntake>();
		builder.Services.AddSingleton<OutgoingDocumentIntake>();
		builder.Services.AddSingleton<PdfUploadStore>();
		builder.Services.AddSingleton<PdfStorageCoordinator>();
		builder.Services.AddHttpClient("OpenAiInvoiceDocument", ConfigureOpenAiInvoiceDocumentClient);
		builder.Services.AddHttpClient("GmailApi", delegate(HttpClient client)
		{
			client.BaseAddress = new Uri("https://gmail.googleapis.com/");
			client.Timeout = TimeSpan.FromSeconds(60);
		});
		builder.Services.AddHttpClient("GoogleOAuth", delegate(HttpClient client)
		{
			client.BaseAddress = new Uri("https://oauth2.googleapis.com/");
			client.Timeout = TimeSpan.FromSeconds(30);
		});
		builder.Services.AddHttpClient("GooglePubSub", delegate(HttpClient client)
		{
			client.BaseAddress = new Uri("https://pubsub.googleapis.com/");
			client.Timeout = TimeSpan.FromSeconds(30);
		});
		builder.Services.AddSingleton<IPdfInvoiceTextExtractor, OpenAiInvoiceDocumentInterpreter>();
		builder.Services.AddSingleton<IncomingValidationProcessor>();
		builder.Services.AddSingleton<SapAttachmentProcessor>();
		builder.Services.AddHostedService<IncomingValidationWorker>();
		builder.Services.AddHostedService<SapAttachmentWorker>();
		builder.Services.AddHostedService<DatevPackageWorker>();
		builder.Services.AddHostedService<DatevTransferAgent>();
		builder.Services.AddHostedService<BttnextLogMonitor>();
		builder.Services.AddHostedService<WeeklyReminderWorker>();
		builder.Services.AddHostedService<BackupWorker>();
		builder.Services.AddHostedService<PdfOrphanCleanupWorker>();
		builder.Services.AddHostedService<SapDiscoveryWorker>();
		builder.Services.AddHostedService<AutomaticBookingStatusWorker>();
		builder.Services.AddHostedService<AutomaticOutgoingInvoiceWorker>();
		if (args.Any(argument => string.Equals(argument, "--gmail-import-latest", StringComparison.OrdinalIgnoreCase)))
			builder.Services.AddHostedService<GmailLatestInvoiceOneShotWorker>();
		else
			builder.Services.AddHostedService<GmailIngestionWorker>();
		builder.Services.AddHttpClient<SapServiceLayerClient>(delegate(HttpClient client)
		{
			string text = builder.Configuration["Sap:Endpoint"] ?? "https://sap.example.invalid/b1s/v2/";
			client.BaseAddress = new Uri(text.EndsWith('/') ? text : (text + "/"));
			client.Timeout = TimeSpan.FromSeconds(20.0);
		}).ConfigurePrimaryHttpMessageHandler((Func<HttpMessageHandler>)delegate
		{
			string uriString = builder.Configuration["Sap:Endpoint"];
			Uri result;
			return (builder.Configuration.GetValue("Sap:TrustServerCertificate", defaultValue: false) && Uri.TryCreate(uriString, UriKind.Absolute, out result) && result.IsLoopback) ? new HttpClientHandler
			{
				ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
			} : new HttpClientHandler();
		});
		builder.Services.AddSingleton<ISapSqlReadClient, SqlSapReadClient>();
		builder.Services.AddSingleton((Func<IServiceProvider, ISapServiceLayerClient>)((IServiceProvider services) => new CompositeSapClient(services.GetRequiredService<SapServiceLayerClient>(), services.GetRequiredService<ISapSqlReadClient>(), builder.Configuration)));
		X509Certificate2 tlsCertificate = LoadServerCertificate(builder.Configuration);
		X509Certificate2 trustedClientRoot = LoadTrustedClientRoot(builder.Configuration);
		bool allowSelfSignedStagingClient = builder.Configuration.GetValue("Tls:AllowSelfSignedClientCertificate", defaultValue: false);
		if (allowSelfSignedStagingClient && !builder.Environment.IsStaging())
		{
			throw new InvalidOperationException("Selbstsignierte Clientzertifikate dürfen ausschließlich im Staging aktiviert werden.");
		}
		builder.WebHost.ConfigureKestrel(delegate(KestrelServerOptions options)
		{
			options.Limits.MaxRequestBodySize = 21037056L;
			options.ConfigureHttpsDefaults(delegate(HttpsConnectionAdapterOptions https)
			{
				https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
			});
			if (tlsCertificate != null)
			{
				int value = builder.Configuration.GetValue("Tls:Port", 5189);
				string text = builder.Configuration["Tls:ListenAddresses"] ?? builder.Configuration["Tls:ListenAddress"];
				IPAddress[] array = ((!string.IsNullOrWhiteSpace(text)) ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(delegate(string ipString)
				{
					if (!IPAddress.TryParse(ipString, out IPAddress address2))
					{
						throw new InvalidOperationException("Tls:ListenAddresses muss nur gültige IP-Adressen enthalten.");
					}
					return address2;
				}).Distinct()
					.ToArray() : new IPAddress[1] { IPAddress.Loopback });
				IPAddress[] array2 = array;
				foreach (IPAddress address in array2)
				{
					options.Listen(address, value, delegate(ListenOptions listen)
					{
						listen.UseHttps(delegate(HttpsConnectionAdapterOptions https)
						{
							https.ServerCertificate = tlsCertificate;
							https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
						});
					});
				}
			}
		});
		CertificateAuthenticationAppBuilderExtensions.AddCertificate(builder.Services.AddAuthentication(delegate(AuthenticationOptions options)
		{
			options.DefaultAuthenticateScheme = "NovaNein";
			options.DefaultChallengeScheme = "NovaNein";
		}).AddPolicyScheme("NovaNein", "NovaNein certificate or web session", delegate(PolicySchemeOptions options)
		{
			options.ForwardDefaultSelector = delegate(HttpContext context)
			{
				if (context.Request.Cookies.ContainsKey("NovaNein.Web"))
				{
					return "Cookies";
				}
				string webAccessMode = builder.Configuration["WebAccess:Mode"] ?? "";
				if (string.Equals(webAccessMode, "WebLogin", StringComparison.OrdinalIgnoreCase))
				{
					bool forwardedBrowserRequest = string.Equals(
						context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault(),
						"http",
						StringComparison.OrdinalIgnoreCase);
					if (forwardedBrowserRequest)
					{
						return "Cookies";
					}
					return context.Connection.ClientCertificate != null ? "Certificate" : "Cookies";
				}
				if (context.Connection.ClientCertificate != null)
				{
					return "Certificate";
				}
				return (!string.Equals(webAccessMode, "TrustedNetwork", StringComparison.OrdinalIgnoreCase)) ? "Certificate" : "NovaNein.TrustedNetwork";
			};
		}).AddScheme<AuthenticationSchemeOptions, TrustedNetworkAuthenticationHandler>("NovaNein.TrustedNetwork", delegate
		{
		})
			.AddCookie("Cookies", delegate(CookieAuthenticationOptions options)
			{
				options.Cookie.Name = "NovaNein.Web";
				options.Cookie.HttpOnly = true;
				options.Cookie.SecurePolicy = builder.Configuration.GetValue("WebAccess:AllowHttpLogin", defaultValue: false)
					? CookieSecurePolicy.SameAsRequest
					: CookieSecurePolicy.Always;
				options.Cookie.SameSite = SameSiteMode.Strict;
				options.ExpireTimeSpan = TimeSpan.FromHours(8.0);
				options.SlidingExpiration = true;
				options.LoginPath = "/login.html";
				options.Events.OnRedirectToLogin = delegate(RedirectContext<CookieAuthenticationOptions> context)
				{
					if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
					{
						context.Response.StatusCode = 401;
						return Task.CompletedTask;
					}
					context.Response.Redirect(context.RedirectUri);
					return Task.CompletedTask;
				};
				options.Events.OnRedirectToAccessDenied = delegate(RedirectContext<CookieAuthenticationOptions> context)
				{
					context.Response.StatusCode = 403;
					return Task.CompletedTask;
				};
			}), (Action<CertificateAuthenticationOptions>)delegate(CertificateAuthenticationOptions options)
		{
			//IL_0038: Unknown result type (might be due to invalid IL or missing references)
			//IL_003d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0062: Unknown result type (might be due to invalid IL or missing references)
			//IL_008c: Expected O, but got Unknown
			options.RevocationMode = X509RevocationMode.NoCheck;
			if (trustedClientRoot != null)
			{
				options.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
				options.CustomTrustStore.Add(trustedClientRoot);
			}
			if (allowSelfSignedStagingClient)
			{
				options.AllowedCertificateTypes = (CertificateTypes)3;
			}
			options.Events = new CertificateAuthenticationEvents
			{
				OnAuthenticationFailed = delegate(CertificateAuthenticationFailedContext context)
				{
					ILogger logger = ((BaseContext<CertificateAuthenticationOptions>)(object)context).HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NovaNein.Server.Mtls");
					logger.LogWarning(context.Exception, "mTLS-Zertifikatauthentifizierung ist fehlgeschlagen.");
					return Task.CompletedTask;
				},
				OnCertificateValidated = async delegate(CertificateValidatedContext context)
				{
					WorkstationRegistry registry = ((BaseContext<CertificateAuthenticationOptions>)(object)context).HttpContext.RequestServices.GetRequiredService<WorkstationRegistry>();
					ILogger logger = ((BaseContext<CertificateAuthenticationOptions>)(object)context).HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NovaNein.Server.Mtls");
					string thumbprint = context.ClientCertificate?.Thumbprint;
					if (await registry.IsRegisteredAsync(thumbprint, ((BaseContext<CertificateAuthenticationOptions>)(object)context).HttpContext.RequestAborted))
					{
						logger.LogInformation("mTLS-Arbeitsplatz {Thumbprint} wurde authentifiziert.", thumbprint);
						// Registered workstation certificates have always been allowed to perform
						// reviewer actions through WebAuthorization. Add the matching formal role
						// claim so policy-protected modules such as automatic booking behave
						// consistently. Admin and master-data roles remain user-login only.
						context.Principal?.AddIdentity(new ClaimsIdentity(
						[
							new Claim(ClaimTypes.Role, "Reviewer"),
							new Claim("novanein:access", "workstation-certificate")
						],
						"NovaNein.WorkstationRole"));
						((ResultContext<CertificateAuthenticationOptions>)(object)context).Success();
					}
					else
					{
						logger.LogWarning("mTLS-Arbeitsplatz {Thumbprint} ist nicht registriert oder wurde gesperrt.", thumbprint ?? "kein Zertifikat");
						((ResultContext<CertificateAuthenticationOptions>)(object)context).Fail("Der Arbeitsplatz ist nicht registriert oder sein Zertifikat wurde gesperrt.");
					}
				}
			};
		});
		builder.Services.AddAuthorization(delegate(AuthorizationOptions options)
		{
			options.FallbackPolicy = new AuthorizationPolicyBuilder("NovaNein").RequireAuthenticatedUser().Build();
			options.AddPolicy("Reviewer", delegate(AuthorizationPolicyBuilder policy)
			{
				policy.RequireAuthenticatedUser().RequireAssertion(context =>
					context.User.IsInRole("Reviewer") ||
					context.User.IsInRole("Admin") ||
					context.User.IsInRole("Manager") ||
					context.User.HasClaim("novanein:permission", WebPermissions.DocumentsReview) ||
					context.User.HasClaim("novanein:permission", WebPermissions.InvoicesPost) ||
					context.User.HasClaim("novanein:permission", WebPermissions.AccountingManage));
			});
			options.AddPolicy("MasterDataApprover", delegate(AuthorizationPolicyBuilder policy)
			{
				policy.RequireAuthenticatedUser().RequireAssertion(context =>
					context.User.IsInRole("MasterDataApprover") ||
					context.User.IsInRole("Admin") ||
					context.User.IsInRole("Manager") ||
					context.User.HasClaim("novanein:permission", WebPermissions.SuppliersManage));
			});
			options.AddPolicy("Admin", delegate(AuthorizationPolicyBuilder policy)
			{
				policy.RequireAuthenticatedUser().RequireRole("Admin", "Manager");
			});
			AddPermissionPolicy(options, "Documents.View", WebPermissions.DocumentsView, "Reviewer");
			AddPermissionPolicy(options, "Documents.Review", WebPermissions.DocumentsReview, "Reviewer");
			AddPermissionPolicy(options, "Invoices.View", WebPermissions.InvoicesView, "Reviewer");
			AddPermissionPolicy(options, "Invoices.Post", WebPermissions.InvoicesPost, "Reviewer");
			AddPermissionPolicy(options, "Suppliers.Manage", WebPermissions.SuppliersManage, "MasterDataApprover");
			AddPermissionPolicy(options, "Accounting.View", WebPermissions.AccountingView, "Reviewer");
			AddPermissionPolicy(options, "Accounting.Manage", WebPermissions.AccountingManage, "Reviewer");
			AddPermissionPolicy(options, "Audit.View", WebPermissions.AuditView, "Reviewer");
			AddPermissionPolicy(options, "Users.Manage", WebPermissions.UsersManage);
			AddPermissionPolicy(options, "Integrations.Manage", WebPermissions.IntegrationsManage);
			AddPermissionPolicy(options, "Paperless.View", WebPermissions.PaperlessView, "Reviewer");
		});
		builder.Services.AddRateLimiter(delegate(RateLimiterOptions options)
		{
			options.RejectionStatusCode = 429;
			options.AddPolicy("web-login", (HttpContext context) => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", (string _) => new FixedWindowRateLimiterOptions
			{
				PermitLimit = 10,
				Window = TimeSpan.FromMinutes(1.0),
				QueueLimit = 0,
				AutoReplenishment = true
			}));
		});
		WebApplication app = builder.Build();
		await app.Services.GetRequiredService<DocumentStore>().InitializeAsync();
		await app.Services.GetRequiredService<WebIdentityStore>().InitializeAsync();
		await app.Services.GetRequiredService<AiReviewAuditStore>().InitializeAsync();
		await app.Services.GetRequiredService<PdfInboxStore>().InitializeAsync();
		await app.Services.GetRequiredService<WorkItemIgnoreStore>().InitializeAsync();
		await app.Services.GetRequiredService<AutomaticBookingStore>().InitializeAsync();
		await app.Services.GetRequiredService<AccountingImportStore>().InitializeAsync();
		await app.Services.GetRequiredService<DatevMappingStore>().InitializeAsync();
		await app.Services.GetRequiredService<WorkstationRegistry>().InitializeAsync();
		await app.Services.GetRequiredService<ReminderStore>().InitializeAsync();
		if (args.Length == 3 && string.Equals(args[0], "--register-workstation", StringComparison.OrdinalIgnoreCase))
		{
			await app.Services.GetRequiredService<WorkstationRegistry>().RegisterAsync(args[1], args[2]);
			Console.WriteLine("Arbeitsplatz '" + args[2] + "' wurde registriert.");
			return;
		}
		if (args.Length == 2 && string.Equals(args[0], "--revoke-workstation", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine((await app.Services.GetRequiredService<WorkstationRegistry>().RevokeAsync(args[1])) ? "Arbeitsplatzzertifikat wurde gesperrt." : "Arbeitsplatzzertifikat war bereits gesperrt oder nicht registriert.");
			return;
		}
		if (args.Length == 2 && string.Equals(args[0], "--verify-workstation", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine((await app.Services.GetRequiredService<WorkstationRegistry>().IsRegisteredAsync(args[1])) ? "Arbeitsplatzzertifikat ist aktiv registriert." : "Arbeitsplatzzertifikat ist nicht aktiv registriert.");
			return;
		}
		if (args.Length == 2 && string.Equals(args[0], "--recalculate-invoice-proposal", StringComparison.OrdinalIgnoreCase))
		{
			if (!Guid.TryParse(args[1], out var proposalId))
				throw new ArgumentException("Die Buchungsvorschlag-ID ist ungültig.", nameof(args));
			var proposal = await app.Services.GetRequiredService<InvoiceProposalService>().RecalculateAsync(proposalId);
			Console.WriteLine(JsonSerializer.Serialize(new
			{
				proposal.Id,
				proposal.Version,
				proposal.InvoiceNumber,
				proposal.SupplierName,
				proposal.Status,
				proposal.Signal,
				proposal.HasGoodsCharacteristics,
				proposal.HasPurchaseOrderReference,
				proposal.Findings
			}));
			return;
		}
		if (args.Length == 2 && string.Equals(args[0], "--reclassify-stored-inventory-proposal", StringComparison.OrdinalIgnoreCase))
		{
			if (!Guid.TryParse(args[1], out var proposalId))
				throw new ArgumentException("Die Buchungsvorschlag-ID ist ungültig.", nameof(args));
			var proposal = await app.Services.GetRequiredService<InvoiceProposalService>().ReclassifyStoredInventoryAsync(proposalId);
			Console.WriteLine(JsonSerializer.Serialize(new
			{
				proposal.Id,
				proposal.Version,
				proposal.InvoiceNumber,
				proposal.SupplierName,
				proposal.Status,
				proposal.Signal,
				proposal.HasGoodsCharacteristics,
				proposal.HasPurchaseOrderReference,
				proposal.Findings
			}));
			return;
		}
		await app.Services.GetRequiredService<TransferEvidenceStore>().InitializeAsync();
		DatevTransferRequestStore transferRequestStore = app.Services.GetRequiredService<DatevTransferRequestStore>();
		await transferRequestStore.InitializeAsync();
		bool autoTransferApprovedInvoices = app.Configuration.GetValue("Datev:AutoTransferApprovedInvoices", defaultValue: false);
		bool autoTransferGreenOnly = app.Configuration.GetValue("Datev:AutoTransferGreenOnly", defaultValue: false);
		if (autoTransferApprovedInvoices || autoTransferGreenOnly)
		{
			if (DateTimeOffset.TryParse(app.Configuration["Datev:AutoTransferNotBeforeUtc"], out DateTimeOffset autoTransferNotBefore))
			{
				int queuedTransfers = autoTransferApprovedInvoices
					? await transferRequestStore.EnsureApprovedInvoicePackagesQueuedAsync(autoTransferNotBefore, "approved-invoice-startup")
					: await transferRequestStore.EnsureGreenPackagesQueuedAsync(autoTransferNotBefore, "green-only-startup");
				if (queuedTransfers > 0)
				{
					app.Logger.LogInformation("{Count} fachlich freigegebene DATEV-Pakete wurden beim Start sicher zur automatischen Übergabe eingereiht.", queuedTransfers);
				}
			}
			else
			{
				app.Logger.LogError("Automatischer DATEV-Transfer bleibt gesperrt, weil Datev:AutoTransferNotBeforeUtc fehlt oder ungültig ist.");
			}
		}
		if (app.Configuration.GetValue("Datev:RebuildPackagesOnStartup", defaultValue: false))
		{
			TransferEvidenceStore transferStore = app.Services.GetRequiredService<TransferEvidenceStore>();
			await transferStore.ClearAllAsync();
			string packageRoot = app.Configuration["Datev:PackageDirectory"];
			if (!string.IsNullOrWhiteSpace(packageRoot) && Directory.Exists(packageRoot))
			{
				foreach (string package in Directory.EnumerateFiles(packageRoot, "*.zip", SearchOption.AllDirectories))
				{
					File.Delete(package);
				}
			}
		}
		DocumentJobQueue jobs = app.Services.GetRequiredService<DocumentJobQueue>();
		await jobs.InitializeAsync();
		await jobs.RecoverInterruptedAsync();
		if (app.Configuration.GetValue("Datev:RebuildPackagesOnStartup", defaultValue: false))
		{
			await jobs.ResetAsync(DocumentJobKind.CreateDatevPackage);
		}
		await app.Services.GetRequiredService<IncomingDocumentIntake>().ReconcileAsync();
		await app.Services.GetRequiredService<OutgoingDocumentIntake>().ReconcileAsync();
		if (app.Services.GetRequiredService<SapAttachmentProcessor>().AutoAttachEnabled())
		{
			foreach (DocumentRecord document in await app.Services.GetRequiredService<DocumentStore>().ListApprovedAsync())
			{
				await jobs.EnsureEnqueuedAsync(document.Id, DocumentJobKind.AttachToSap);
			}
		}
		if (app.Configuration.GetValue("Datev:AutoPreparePackages", defaultValue: false) && app.Configuration.GetValue("Datev:BackfillApprovedOnStartup", defaultValue: false))
		{
			foreach (DocumentRecord document2 in await app.Services.GetRequiredService<DocumentStore>().ListAttachedToSapAsync())
			{
				if (document2.Sap.Type.IsInvoice())
				{
					await jobs.EnsureEnqueuedAsync(document2.Id, DocumentJobKind.CreateDatevPackage);
				}
			}
		}
		app.Use(async delegate(HttpContext context, Func<Task> next)
		{
			context.Response.Headers["X-Content-Type-Options"] = "nosniff";
			context.Response.Headers["Referrer-Policy"] = "no-referrer";
			context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'";
			if (context.Request.IsHttps)
			{
				context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
			}
			await next();
		});
		app.UseDefaultFiles();
		app.UseStaticFiles();
		app.UseRateLimiter();
		app.UseAuthentication();
		app.UseAuthorization();
		app.Use(async delegate(HttpContext context, Func<Task> next)
		{
			bool passwordChangeRequired =
				string.Equals(context.User.Identity?.AuthenticationType, "Cookies", StringComparison.Ordinal) &&
				string.Equals(context.User.FindFirstValue("novanein:must-change-password"), "true", StringComparison.Ordinal);
			bool passwordChangePath =
				context.Request.Path.StartsWithSegments("/auth/change-password") ||
				context.Request.Path.StartsWithSegments("/auth/me") ||
				context.Request.Path.StartsWithSegments("/auth/logout") ||
				context.Request.Path.StartsWithSegments("/auth/csrf");
			if (passwordChangeRequired && context.Request.Path.StartsWithSegments("/api") && !passwordChangePath)
			{
				context.Response.StatusCode = StatusCodes.Status403Forbidden;
				await context.Response.WriteAsJsonAsync(new { error = "Bitte zuerst das temporäre Kennwort ändern.", mustChangePassword = true });
				return;
			}
			bool browserIdentity = string.Equals(context.User.Identity?.AuthenticationType, "Cookies", StringComparison.Ordinal) || string.Equals(context.User.Identity?.AuthenticationType, "NovaNein.TrustedNetwork", StringComparison.Ordinal);
			if ((HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)) && (context.Request.Path.StartsWithSegments("/auth") || browserIdentity))
			{
				string expected = context.Request.Cookies["NovaNein.Csrf"];
				string supplied = context.Request.Headers["X-CSRF-Token"].FirstOrDefault();
				if (!WebCsrf.Matches(expected, supplied))
				{
					context.Response.StatusCode = 400;
					await context.Response.WriteAsJsonAsync(new
					{
						error = "Die Sicherheitsprüfung für dieses Formular ist abgelaufen. Bitte die Seite neu laden."
					});
					return;
				}
			}
			await next();
		});
		app.MapGet("/", (Func<IResult>)(() => Results.Redirect("/index.html"))).AllowAnonymous();
		app.MapGet("/auth/csrf", (Func<HttpContext, IResult>)delegate(HttpContext context)
		{
			string text = WebCsrf.CreateToken();
			context.Response.Cookies.Append("NovaNein.Csrf", text, new CookieOptions
			{
				HttpOnly = false,
				Secure = !app.Configuration.GetValue("WebAccess:AllowHttpLogin", defaultValue: false),
				SameSite = SameSiteMode.Strict,
				IsEssential = true,
				Path = "/"
			});
			return Results.Ok(new
			{
				token = text
			});
		}).AllowAnonymous();
		app.MapPost("/auth/login", (Func<WebLoginRequest, HttpContext, WebIdentityStore, CancellationToken, Task<IResult>>)async delegate(WebLoginRequest request, HttpContext context, WebIdentityStore identities, CancellationToken ct)
		{
			WebUser user = await identities.AuthenticateAsync(request.UserName, request.Password, context.Connection.RemoteIpAddress?.ToString(), ct);
			if ((object)user == null)
			{
				return Results.Unauthorized();
			}
			Claim[] claims = CreateUserClaims(user);
			await context.SignInAsync("Cookies", new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")));
			return Results.Ok(new
			{
				userName = user.UserName,
				displayName = user.DisplayName,
				role = user.Role,
				mustChangePassword = user.MustChangePassword
			});
		}).AllowAnonymous().RequireRateLimiting("web-login");
		app.MapPost("/auth/logout", (Func<HttpContext, Task<IResult>>)async delegate(HttpContext context)
		{
			await context.SignOutAsync("Cookies");
			return Results.NoContent();
		}).RequireAuthorization();
		app.MapGet("/auth/me", (Func<ClaimsPrincipal, IResult>)((ClaimsPrincipal principal) => Results.Ok(new
		{
			userName = principal.Identity?.Name,
			displayName = principal.FindFirstValue("novanein:display-name") ?? principal.Identity?.Name,
			email = principal.FindFirstValue(ClaimTypes.Email) ?? "",
			role = (principal.FindFirstValue("http://schemas.microsoft.com/ws/2008/06/identity/claims/role") ?? "Operator"),
			roleLabel = WebPermissions.RoleLabel(principal.FindFirstValue(ClaimTypes.Role) ?? "Operator"),
			permissions = principal.FindAll("novanein:permission").Select(claim => claim.Value).ToArray(),
			mustChangePassword = string.Equals(principal.FindFirstValue("novanein:must-change-password"), "true", StringComparison.Ordinal),
			accessMode = (principal.FindFirstValue("novanein:access") ?? "session")
		}))).RequireAuthorization();
		app.MapPost("/auth/change-password", async (WebPasswordChangeRequest request, HttpContext context, WebIdentityStore identities, CancellationToken ct) =>
		{
			if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id))
			{
				return Results.BadRequest(new { error = "Diese Sitzung unterstützt keinen Kennwortwechsel." });
			}
			try
			{
				WebUser user = await identities.ChangePasswordAsync(id, request.CurrentPassword, request.NewPassword, context.Connection.RemoteIpAddress?.ToString(), ct);
				await context.SignInAsync("Cookies", new ClaimsPrincipal(new ClaimsIdentity(CreateUserClaims(user), "Cookies")));
				return Results.Ok(new { changed = true });
			}
			catch (UnauthorizedAccessException exception)
			{
				return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status401Unauthorized);
			}
			catch (ArgumentException exception)
			{
				return Results.BadRequest(new { error = exception.Message });
			}
		}).RequireAuthorization();
		app.MapGet("/api/v1/invoice-proposals", async (string? status, AutomaticBookingStore store, CancellationToken ct) =>
			Results.Ok(await store.ListProposalsAsync(status, ct))).RequireAuthorization("Invoices.View");
		app.MapGet("/api/v1/invoice-proposals/{id:guid}", async (Guid id, AutomaticBookingStore store, CancellationToken ct) =>
		{
			var proposal = await store.GetProposalAsync(id, ct);
			return proposal == null ? Results.NotFound() : Results.Ok(proposal);
		}).RequireAuthorization("Invoices.View");
		app.MapGet("/api/v1/sap/accounts/{account}", async (string account, ISapServiceLayerClient sap, CancellationToken ct) =>
		{
			try
			{
				var validation = await sap.ValidateAccountAsync(account, ct);
				return Results.Ok(new
				{
					account = validation.Account,
					name = validation.Name,
					exists = validation.Exists,
					active = validation.Active,
					requiresDimensions = validation.RequiresDimensions,
					error = validation.Error
				});
			}
			catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or SqlException)
			{
				return Results.Problem(exception.Message, statusCode: 502);
			}
		}).RequireAuthorization("Invoices.View");
		app.MapGet("/api/v1/invoice-proposals/{id:guid}/file", async (Guid id, AutomaticBookingStore store, IConfiguration configuration, CancellationToken ct) =>
		{
			var proposal = await store.GetProposalAsync(id, ct);
			var attachment = proposal?.MailSource?.Attachments?.SingleOrDefault(item => item.Id == proposal.MailAttachmentId);
			if (attachment == null) return Results.NotFound();
			var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuration["Storage:DocumentRoot"] ?? "data/documents")) + Path.DirectorySeparatorChar;
			var fullPath = Path.GetFullPath(attachment.LocalPath);
			if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
				return Results.NotFound();
			return Results.File(fullPath, "application/pdf", enableRangeProcessing: true);
		}).RequireAuthorization("Invoices.View");
		app.MapPost("/api/v1/invoice-proposals/{id:guid}/recalculate", async (Guid id, InvoiceProposalService service, CancellationToken ct) =>
		{
			try { return Results.Ok(await service.RecalculateAsync(id, ct)); }
			catch (KeyNotFoundException) { return Results.NotFound(); }
			catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
		}).RequireAuthorization("Invoices.Post");
		app.MapPost("/api/v1/invoice-proposals/{id:guid}/reject", async (Guid id, InvoiceProposalDecisionRequest request, HttpRequest http, InvoiceProposalService service, CancellationToken ct) =>
		{
			try { return Results.Ok(await service.RejectAsync(id, request, Actor(http), ct)); }
			catch (KeyNotFoundException) { return Results.NotFound(); }
			catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
			catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
		}).RequireAuthorization("Invoices.Post");
		app.MapPost("/api/v1/invoice-proposals/{id:guid}/approve-and-post", async (Guid id, InvoiceProposalDecisionRequest request, HttpRequest http, InvoiceProposalService service, CancellationToken ct) =>
		{
			try { return Results.Ok(await service.ApproveAndPostAsync(id, request, Actor(http), ct)); }
			catch (KeyNotFoundException) { return Results.NotFound(); }
			catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
			catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
			catch (HttpRequestException exception) { return Results.Problem(exception.Message, statusCode: 502); }
		}).RequireAuthorization("Invoices.Post");
		app.MapGet("/api/v1/supplier-proposals", async (string? status, AutomaticBookingStore store, CancellationToken ct) =>
			Results.Ok(await store.ListSupplierProposalsAsync(status, ct))).RequireAuthorization("Suppliers.Manage");
		app.MapPost("/api/v1/supplier-proposals/{id:guid}/approve-and-create", async (Guid id, SupplierProposalApprovalRequest request, HttpRequest http, InvoiceProposalService service, CancellationToken ct) =>
		{
			try { return Results.Ok(await service.ApproveAndCreateSupplierAsync(id, request, Actor(http), ct)); }
			catch (KeyNotFoundException) { return Results.NotFound(); }
			catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
			catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
			catch (HttpRequestException exception) { return Results.Problem(exception.Message, statusCode: 502); }
		}).RequireAuthorization("Suppliers.Manage");
		app.MapGet("/api/v1/admin/gmail/status", async (GmailIngestionService service, CancellationToken ct) =>
			Results.Ok(await service.GetStatusAsync(ct))).RequireAuthorization("Integrations.Manage");
		app.MapPost("/api/v1/admin/gmail/sync", async (GmailIngestionService service, CancellationToken ct) =>
		{
			try { return Results.Ok(await service.SyncAsync(ct)); }
			catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
			catch (HttpRequestException exception) { return Results.Problem(exception.Message, statusCode: 502); }
		}).RequireAuthorization("Integrations.Manage");
		app.MapGet("/api/v1/paperless/documents", (Func<int?, int?, string, PaperlessClient, CancellationToken, Task<IResult>>)async delegate(int? page, int? pageSize, string? query, PaperlessClient paperless, CancellationToken ct)
		{
			try
			{
				return Results.Ok(await paperless.ListAsync(page ?? 1, pageSize ?? 50, query, ct));
			}
			catch (PaperlessConfigurationException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
			catch (PaperlessAuthenticationException ex2)
			{
				int? statusCode = 502;
				return Results.Problem(ex2.Message, null, statusCode);
			}
			catch (HttpRequestException ex3)
			{
				return Results.Problem("Paperless ist derzeit nicht erreichbar.", null, 503, null, null, new Dictionary<string, object> { ["reason"] = ex3.Message });
			}
		}).RequireAuthorization("Paperless.View");
		app.MapGet("/api/v1/paperless/documents/{id:int}", (Func<int, PaperlessClient, CancellationToken, Task<IResult>>)async delegate(int id, PaperlessClient paperless, CancellationToken ct)
		{
			try
			{
				return Results.Ok(await paperless.GetAsync(id, ct));
			}
			catch (PaperlessConfigurationException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
			catch (PaperlessAuthenticationException ex2)
			{
				int? statusCode = 502;
				return Results.Problem(ex2.Message, null, statusCode);
			}
			catch (HttpRequestException)
			{
				return Results.Problem("Paperless ist derzeit nicht erreichbar.", null, 503);
			}
			catch (KeyNotFoundException)
			{
				return Results.NotFound();
			}
		}).RequireAuthorization("Paperless.View");
		app.MapGet("/api/v1/paperless/documents/{id:int}/file", (Func<int, PaperlessClient, CancellationToken, Task<IResult>>)async delegate(int id, PaperlessClient paperless, CancellationToken ct)
		{
			try
			{
				return Results.File(await paperless.DownloadAsync(id, ct), "application/pdf", $"paperless-{id}.pdf");
			}
			catch (PaperlessConfigurationException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
			catch (PaperlessAuthenticationException ex2)
			{
				int? statusCode = 502;
				return Results.Problem(ex2.Message, null, statusCode);
			}
			catch (HttpRequestException)
			{
				return Results.Problem("Paperless ist derzeit nicht erreichbar.", null, 503);
			}
		}).RequireAuthorization("Paperless.View");
		app.MapGet("/api/v1/paperless/matches/{kind}/{docEntry:int}", (Func<string, int, PaperlessClient, ISapServiceLayerClient, CancellationToken, Task<IResult>>)async delegate(string kind, int docEntry, PaperlessClient paperless, ISapServiceLayerClient sap, CancellationToken ct)
		{
			if (!TryParseSapDocumentKind(kind, out var documentKind))
			{
				return Results.BadRequest(new
				{
					error = "Die SAP-Belegart ist unbekannt."
				});
			}
			try
			{
				return Results.Ok(await paperless.FindCandidatesAsync(await sap.GetDocumentAsync(documentKind, docEntry, ct), ct));
			}
			catch (PaperlessConfigurationException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
			catch (PaperlessAuthenticationException ex2)
			{
				int? statusCode = 502;
				return Results.Problem(ex2.Message, null, statusCode);
			}
			catch (KeyNotFoundException)
			{
				return Results.NotFound();
			}
		}).RequireAuthorization("Paperless.View");
		app.MapGet("/api/v1/admin/permissions", () => Results.Ok(new
		{
			roles = new[] { "Operator", "Reviewer", "MasterDataApprover", "Admin", "Manager" }.Select(role => new
			{
				key = role,
				label = WebPermissions.RoleLabel(role),
				defaultPermissions = WebPermissions.DefaultsForRole(role)
			}),
			permissions = WebPermissions.Catalog
		})).RequireAuthorization("Users.Manage");
		app.MapGet("/api/v1/admin/users", (Func<WebIdentityStore, CancellationToken, Task<IResult>>)(async (WebIdentityStore identities, CancellationToken ct) => Results.Ok(await identities.ListAsync(ct)))).RequireAuthorization("Users.Manage");
		app.MapGet("/api/v1/admin/user-audit", async (int? limit, WebIdentityStore identities, CancellationToken ct) =>
			Results.Ok(await identities.ListAuditAsync(limit ?? 100, ct))).RequireAuthorization("Audit.View");
		app.MapGet("/api/v1/admin/datev-mappings", (Func<DatevMappingStore, CancellationToken, Task<IResult>>)(async (DatevMappingStore mappings, CancellationToken ct) => Results.Ok(await mappings.ListAsync(ct)))).RequireAuthorization("Integrations.Manage");
		app.MapPost("/api/v1/admin/datev-mappings", (Func<DatevBookingMapping, HttpRequest, DatevMappingStore, CancellationToken, Task<IResult>>)async delegate(DatevBookingMapping mapping, HttpRequest request, DatevMappingStore mappings, CancellationToken ct)
		{
			try
			{
				return Results.Ok(await mappings.UpsertAsync(mapping, request.HttpContext.User.Identity?.Name ?? "", ct));
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
		}).RequireAuthorization("Integrations.Manage");
		app.MapPost("/api/v1/admin/users", async (WebUserRequest request, HttpContext context, WebIdentityStore identities, CancellationToken ct) =>
		{
			try
			{
				WebUserProvisioningResult result = await identities.CreateAsync(request, context.User.Identity?.Name ?? "unbekannt", ct);
				return Results.Created($"/api/v1/admin/users/{result.User.Id}", result);
			}
			catch (ArgumentException exception)
			{
				return Results.BadRequest(new { error = exception.Message });
			}
			catch (InvalidOperationException exception)
			{
				return Results.Conflict(new { error = exception.Message });
			}
		}).RequireAuthorization("Users.Manage");
		app.MapPut("/api/v1/admin/users/{id:guid}", async (Guid id, WebUserUpdateRequest request, HttpContext context, WebIdentityStore identities, CancellationToken ct) =>
		{
			try
			{
				return Results.Ok(await identities.UpdateAsync(id, request, context.User.Identity?.Name ?? "unbekannt", ct));
			}
			catch (KeyNotFoundException)
			{
				return Results.NotFound();
			}
			catch (ArgumentException exception)
			{
				return Results.BadRequest(new { error = exception.Message });
			}
			catch (InvalidOperationException exception)
			{
				return Results.Conflict(new { error = exception.Message });
			}
		}).RequireAuthorization("Users.Manage");
		app.MapPost("/api/v1/admin/users/{id:guid}/reset-password", async (Guid id, WebPasswordResetRequest request, HttpContext context, WebIdentityStore identities, CancellationToken ct) =>
		{
			try
			{
				return Results.Ok(await identities.ResetPasswordAsync(id, request.MustChangePassword, context.User.Identity?.Name ?? "unbekannt", ct));
			}
			catch (KeyNotFoundException)
			{
				return Results.NotFound();
			}
		}).RequireAuthorization("Users.Manage");
		app.MapPost("/api/v1/accounting-imports/datev-bookings", (Func<HttpRequest, HttpContext, AccountingImportStore, CockpitStatusNotifier, CancellationToken, Task<IResult>>)async delegate(HttpRequest request, HttpContext context, AccountingImportStore store, CockpitStatusNotifier notifier, CancellationToken ct)
		{
			if (!request.HasFormContentType)
			{
				return Results.BadRequest(new
				{
					error = "Bitte eine DATEV-CSV als Formulardatei hochladen."
				});
			}
			IFormCollection form = await request.ReadFormAsync(ct);
			IFormFile file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
			if (file == null)
			{
				return Results.BadRequest(new
				{
					error = "Es wurde keine DATEV-CSV ausgewählt."
				});
			}
			if (file.Length > 52428800)
			{
				return Results.BadRequest(new
				{
					error = "Die DATEV-CSV überschreitet 50 MB."
				});
			}
			try
			{
				IResult result;
				await using (Stream stream = file.OpenReadStream())
				{
					using MemoryStream memory = new MemoryStream();
					await stream.CopyToAsync(memory, ct);
					AccountingImportBatch batch = await store.ImportAsync(Path.GetFileName(file.FileName), memory.ToArray(), context.User.Identity?.Name ?? "unbekannt", ct);
					await notifier.ChangedAsync("AccountingImportCreated", new { batch.Id }, ct);
					result = Results.Created($"/api/v1/accounting-imports/{batch.Id}", batch);
				}
				return result;
			}
			catch (Exception ex) when (((ex is InvalidDataException || ex is InvalidOperationException) ? 1 : 0) != 0)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
		}).RequireAuthorization("Accounting.Manage");
		app.MapGet("/api/v1/accounting-imports", (Func<AccountingImportStore, CancellationToken, Task<IResult>>)(async (AccountingImportStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)))).RequireAuthorization("Accounting.View");
		app.MapGet("/api/v1/accounting-imports/{id:guid}", (Func<Guid, AccountingImportStore, CancellationToken, Task<IResult>>)async delegate(Guid id, AccountingImportStore store, CancellationToken ct)
		{
			AccountingImportBatch batch = await store.GetAsync(id, includeRows: true, ct);
			return ((object)batch != null) ? Results.Ok(batch) : Results.NotFound();
		}).RequireAuthorization("Accounting.View");
		app.MapPost("/api/v1/accounting-imports/{id:guid}/confirm", (Func<Guid, HttpContext, AccountingImportStore, CockpitStatusNotifier, CancellationToken, Task<IResult>>)async delegate(Guid id, HttpContext context, AccountingImportStore store, CockpitStatusNotifier notifier, CancellationToken ct)
		{
			_ = 1;
			try
			{
				AccountingImportBatch batch = await store.ConfirmAsync(id, context.User.Identity?.Name ?? "unbekannt", ct);
				await notifier.ChangedAsync("AccountingImportConfirmed", new { batch.Id }, ct);
				return Results.Ok(batch);
			}
			catch (InvalidOperationException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
		}).RequireAuthorization("Accounting.Manage");
		app.MapGet("/api/v1/reconciliation", (Func<Guid?, string, int?, int?, ReconciliationService, CancellationToken, Task<IResult>>)async delegate(Guid? batchId, string? status, int? page, int? pageSize, ReconciliationService service, CancellationToken ct)
		{
			try
			{
				ReconciliationStatus? parsed = null;
				if (!string.IsNullOrWhiteSpace(status))
				{
					if (!Enum.TryParse<ReconciliationStatus>(status, ignoreCase: true, out var parsedValue))
					{
						return Results.BadRequest(new
						{
							error = "Unbekannter Abgleichstatus."
						});
					}
					parsed = parsedValue;
				}
				return Results.Ok(await service.ListAsync(batchId, parsed, page ?? 1, pageSize ?? 50, ct));
			}
			catch (InvalidOperationException ex)
			{
				return Results.Conflict(new
				{
					error = ex.Message
				});
			}
		}).RequireAuthorization("Accounting.View");
		app.MapGet("/api/v1/reconciliation/export.csv", (Func<ReconciliationService, CancellationToken, Task<IResult>>)async delegate(ReconciliationService service, CancellationToken ct)
		{
			try
			{
				ReconciliationPage page = await service.ListAsync(null, null, 1, 20000, ct);
				List<string> lines = new List<string> { "Status;Richtung;SAP-DocEntry;SAP-DocNum;Rechnungsnummer;Partner;SAP-Betrag;SAP-Währung;DATEV-Betrag;DATEV-Währung;DATEV-Konto;Gründe" };
				lines.AddRange(page.Items.Select((ReconciliationItem x) => string.Join(';', Csv(x.Status), Csv(x.Direction), Csv(x.DocEntry), Csv(x.DocNum), Csv(x.InvoiceNumber), Csv(x.BusinessPartner), Csv(x.SapAmount), Csv(x.SapCurrency), Csv(x.DatevAmount), Csv(x.DatevCurrency), Csv(x.DatevAccount), Csv(string.Join(" ", x.Reasons)))));
				return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", lines))).ToArray(), "text/csv; charset=utf-8", "NovaNein-Abgleich.csv");
			}
			catch (InvalidOperationException ex)
			{
				return Results.Conflict(new
				{
					error = ex.Message
				});
			}
		}).RequireAuthorization("Accounting.View");
		app.MapGet("/api/v1/reconciliation/{id}", (Func<string, ReconciliationService, CancellationToken, Task<IResult>>)async delegate(string id, ReconciliationService service, CancellationToken ct)
		{
			ReconciliationItem item = await service.GetAsync(id, ct);
			return ((object)item != null) ? Results.Ok(item) : Results.NotFound();
		}).RequireAuthorization("Accounting.View");
		app.MapPost("/api/v1/reconciliation/{id}/decisions", (Func<string, ReconciliationDecisionRequest, HttpContext, ReconciliationService, CockpitStatusNotifier, CancellationToken, Task<IResult>>)async delegate(string id, ReconciliationDecisionRequest request, HttpContext context, ReconciliationService service, CockpitStatusNotifier notifier, CancellationToken ct)
		{
			_ = 1;
			try
			{
				ReconciliationItem item = await service.DecideAsync(id, request, context.User.Identity?.Name ?? "unbekannt", ct);
				await notifier.ChangedAsync("ReconciliationDecided", new { item.Id }, ct);
				return Results.Ok(item);
			}
			catch (KeyNotFoundException)
			{
				return Results.NotFound();
			}
			catch (Exception ex2) when (((ex2 is ArgumentException || ex2 is InvalidOperationException) ? 1 : 0) != 0)
			{
				return Results.BadRequest(new
				{
					error = ex2.Message
				});
			}
		}).RequireAuthorization("Accounting.Manage");
		if (app.Configuration.GetValue("WebAccess:SignalREnabled", defaultValue: true))
		{
			app.MapHub<CockpitStatusHub>("/hubs/status");
		}
		app.MapGet("/api/v1/work-items", (Func<DateOnly?, DateOnly?, DateOnly?, DateOnly?, string, string, bool?, string, bool?, int?, int?, string, string, string, WorkItemService, CancellationToken, Task<IResult>>)async delegate(DateOnly? fromEntryDate, DateOnly? toEntryDate, DateOnly? from, DateOnly? to, string? direction, string? status, bool? pdfPresent, string? datevStatus, bool? errorStatus, int? page, int? pageSize, string? sortBy, string? sortDirection, string? search, WorkItemService service, CancellationToken ct)
		{
			try
			{
				return Results.Ok(await service.ListAsync(new WorkItemQuery(fromEntryDate ?? from, toEntryDate ?? to, direction, status, pdfPresent, datevStatus, errorStatus, page ?? 1, pageSize ?? 50, sortBy, sortDirection, search), ct));
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				return Results.Problem("SAP ist für die Aufgabenliste vorübergehend nicht erreichbar.", null, 503);
			}
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/work-items/summary", (Func<DateOnly?, DateOnly?, string, WorkItemService, CancellationToken, Task<IResult>>)async delegate(DateOnly? fromEntryDate, DateOnly? toEntryDate, string? direction, WorkItemService service, CancellationToken ct)
		{
			try
			{
				return Results.Ok(await service.SummaryAsync(new WorkItemQuery(fromEntryDate, toEntryDate, direction), ct));
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				return Results.Problem("SAP ist für die Zusammenfassung vorübergehend nicht erreichbar.", null, 503);
			}
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/work-items/{sapKind}/{docEntry:int}/ignore-history", (Func<string, int, WorkItemIgnoreStore, CancellationToken, Task<IResult>>)async delegate(string sapKind, int docEntry, WorkItemIgnoreStore ignoreStore, CancellationToken ct)
		{
			if (!TryParseSapDocumentKind(sapKind, out var kind))
			{
				return Results.BadRequest(new { error = "Die SAP-Belegart ist unbekannt." });
			}
			if (docEntry <= 0) return Results.BadRequest(new { error = "Der SAP-Belegschlüssel ist ungültig." });
			return Results.Ok(await ignoreStore.HistoryAsync(kind, docEntry, ct));
		}).RequireAuthorization("Documents.View");
		app.MapPost("/api/v1/work-items/{sapKind}/{docEntry:int}/ignore", (Func<string, int, WorkItemIgnoreRequest, HttpContext, WebIdentityStore, WorkItemIgnoreStore, CockpitStatusNotifier, CancellationToken, Task<IResult>>)async delegate(string sapKind, int docEntry, WorkItemIgnoreRequest request, HttpContext context, WebIdentityStore identities, WorkItemIgnoreStore ignoreStore, CockpitStatusNotifier notifier, CancellationToken ct)
		{
			if (!TryParseSapDocumentKind(sapKind, out var kind))
			{
				return Results.BadRequest(new { error = "Die SAP-Belegart ist unbekannt." });
			}
			WebUser? admin = await identities.AuthenticateAsync(request.AdminUserName, request.AdminPassword, context.Connection.RemoteIpAddress?.ToString(), ct);
			if (admin == null)
			{
				return Results.Json(new { error = "Admin-Benutzername oder Kennwort ist nicht korrekt." }, statusCode: StatusCodes.Status401Unauthorized);
			}
			if (!WebPermissions.Has(admin, WebPermissions.UsersManage))
			{
				return Results.Json(new { error = "Für diese Aktion ist ein NovaNein-Administrator erforderlich." }, statusCode: StatusCodes.Status403Forbidden);
			}
			try
			{
				WorkItemIgnoreEntry entry = await ignoreStore.IgnoreAsync(kind, docEntry, request.DocNum, request.Reason, admin.UserName, ct);
				await notifier.ChangedAsync("WorkItemIgnored", new { sapKind = kind.ToString(), docEntry }, ct);
				return Results.Ok(entry);
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new { error = ex.Message });
			}
		}).RequireAuthorization("Documents.Review").RequireRateLimiting("web-login");
		app.MapPost("/api/v1/work-items/{sapKind}/{docEntry:int}/restore", (Func<string, int, WorkItemIgnoreRequest, HttpContext, WebIdentityStore, WorkItemIgnoreStore, CockpitStatusNotifier, CancellationToken, Task<IResult>>)async delegate(string sapKind, int docEntry, WorkItemIgnoreRequest request, HttpContext context, WebIdentityStore identities, WorkItemIgnoreStore ignoreStore, CockpitStatusNotifier notifier, CancellationToken ct)
		{
			if (!TryParseSapDocumentKind(sapKind, out var kind))
			{
				return Results.BadRequest(new { error = "Die SAP-Belegart ist unbekannt." });
			}
			WebUser? admin = await identities.AuthenticateAsync(request.AdminUserName, request.AdminPassword, context.Connection.RemoteIpAddress?.ToString(), ct);
			if (admin == null)
			{
				return Results.Json(new { error = "Admin-Benutzername oder Kennwort ist nicht korrekt." }, statusCode: StatusCodes.Status401Unauthorized);
			}
			if (!WebPermissions.Has(admin, WebPermissions.UsersManage))
			{
				return Results.Json(new { error = "Für diese Aktion ist ein NovaNein-Administrator erforderlich." }, statusCode: StatusCodes.Status403Forbidden);
			}
			try
			{
				if (!await ignoreStore.RestoreAsync(kind, docEntry, request.DocNum, request.Reason, admin.UserName, ct))
				{
					return Results.NotFound(new { error = "Für diesen SAP-Beleg ist keine aktive Ignorierung gespeichert." });
				}
				await notifier.ChangedAsync("WorkItemRestored", new { sapKind = kind.ToString(), docEntry }, ct);
				return Results.NoContent();
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new { error = ex.Message });
			}
		}).RequireAuthorization("Documents.Review").RequireRateLimiting("web-login");
		app.MapGet("/api/v1/pdf-inbox", (Func<PdfInboxService, CancellationToken, Task<IResult>>)(async (PdfInboxService inbox, CancellationToken ct) => Results.Ok(await inbox.ListAsync(PdfInboxStatus.Unassigned, ct)))).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/pdf-inbox/{id:guid}/suggestions", (Func<Guid, PdfInboxService, CancellationToken, Task<IResult>>)async delegate(Guid id, PdfInboxService inbox, CancellationToken ct)
		{
			try
			{
				return Results.Ok(await inbox.SuggestAsync(id, ct));
			}
			catch (KeyNotFoundException ex)
			{
				return Results.NotFound(new
				{
					error = ex.Message
				});
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				return Results.Problem("SAP ist für Vorschläge vorübergehend nicht erreichbar.", null, 503);
			}
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/pdf-inbox/{id:guid}/file", (Func<Guid, PdfInboxStore, IConfiguration, CancellationToken, Task<IResult>>)async delegate(Guid id, PdfInboxStore inbox, IConfiguration configuration, CancellationToken ct)
		{
			PdfInboxItem item = await inbox.GetAsync(id, ct);
			if ((object)item == null || string.Equals(item.Status, "rejected", StringComparison.OrdinalIgnoreCase))
			{
				return Results.NotFound();
			}
			string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuration["Storage:DocumentRoot"] ?? "data/documents"));
			string prefix = root + Path.DirectorySeparatorChar;
			string path = Path.GetFullPath(Path.Combine(root, item.Sha256 + ".pdf"));
			return (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) ? Results.File(path, "application/pdf", null, null, null, enableRangeProcessing: true) : Results.NotFound();
		}).RequireAuthorization("Documents.View");
		app.MapPost("/api/v1/pdf-inbox", (Func<HttpRequest, PdfInboxService, CancellationToken, Task<IResult>>)async delegate(HttpRequest request, PdfInboxService inbox, CancellationToken ct)
		{
			if (!request.HasFormContentType)
			{
				return Results.BadRequest(new
				{
					error = "multipart/form-data mit genau einer PDF ist erforderlich."
				});
			}
			IFormCollection form = await request.ReadFormAsync(ct);
			IFormFile file = form.Files.GetFile("pdf");
			if (file == null)
			{
				return Results.BadRequest(new
				{
					error = "Genau eine PDF-Datei ist erforderlich."
				});
			}
			try
			{
				IResult result;
				await using (Stream stream = file.OpenReadStream())
				{
					bool skipExtraction = bool.TryParse(form["skipExtraction"].ToString(), out bool parsedSkipExtraction) && parsedSkipExtraction;
					PdfInboxItem item = await inbox.UploadAsync(file.FileName, file.Length, stream, Actor(request), !skipExtraction, ct);
					result = Results.Created($"/api/v1/pdf-inbox/{item.Id:D}", item);
				}
				return result;
			}
			catch (PdfInboxDuplicateException ex)
			{
				return Results.Conflict(new
				{
					error = ex.Message
				});
			}
			catch (PdfUploadTooLargeException ex2)
			{
				return Results.Json(new
				{
					error = ex2.Message
				}, (JsonSerializerOptions?)null, (string?)null, (int?)413);
			}
			catch (InvalidPdfUploadException ex3)
			{
				return Results.BadRequest(new
				{
					error = ex3.Message
				});
			}
		}).RequireAuthorization("Documents.Review");
		app.MapPost("/api/v1/pdf-inbox/{id:guid}/assign", (Func<Guid, PdfInboxAssignmentRequest, PdfInboxService, HttpRequest, CancellationToken, Task<IResult>>)async delegate(Guid id, PdfInboxAssignmentRequest assignment, PdfInboxService inbox, HttpRequest request, CancellationToken ct)
		{
			string kindValue = (string.IsNullOrWhiteSpace(assignment.SapKind) ? assignment.Direction : assignment.SapKind);
			if (!TryParseSapDocumentKind(kindValue, out var kind))
			{
				return Results.BadRequest(new
				{
					error = "Unbekannte SAP-Belegart."
				});
			}
			if (assignment.DocEntry <= 0 || assignment.DocNum <= 0)
			{
				return Results.BadRequest(new
				{
					error = "DocEntry und DocNum müssen positiv sein."
				});
			}
			try
			{
				return Results.Ok(await inbox.AssignAsync(id, kind, assignment.DocEntry, assignment.DocNum, Actor(request), ct));
			}
			catch (KeyNotFoundException ex)
			{
				return Results.NotFound(new
				{
					error = ex.Message
				});
			}
			catch (PdfInboxAlreadyAssignedException ex2)
			{
				return Results.Conflict(new
				{
					error = ex2.Message
				});
			}
			catch (PdfInboxDuplicateException ex3)
			{
				return Results.Conflict(new
				{
					error = ex3.Message
				});
			}
			catch (InvalidOperationException ex4)
			{
				return Results.Conflict(new
				{
					error = ex4.Message
				});
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				return Results.Problem("Der SAP-Beleg konnte nicht erneut gelesen werden.", null, 503);
			}
		}).RequireAuthorization("Documents.Review");
		app.MapGet("/health", (Func<IConfiguration, ISapServiceLayerClient, SapAttachmentProcessor, ILoggerFactory, CancellationToken, Task<IResult>>)async delegate(IConfiguration configuration, ISapServiceLayerClient sapClient, SapAttachmentProcessor attachmentProcessor, ILoggerFactory loggerFactory, CancellationToken ct)
		{
			bool sapConfigured = IsSapReadConfigured(configuration);
			bool openAiConfigured = !string.IsNullOrWhiteSpace(configuration["OpenAI:ApiKey"]);
			string documentRoot = configuration["Storage:DocumentRoot"] ?? "data/documents";
			bool storageReady = Directory.Exists(documentRoot);
			string datevWatchfolder = configuration["Datev:WatchFolder"];
			string datevPackageDirectory = configuration["Datev:PackageDirectory"];
			bool packageDirectoryReady = !string.IsNullOrWhiteSpace(datevPackageDirectory) && Path.IsPathFullyQualified(datevPackageDirectory) && Directory.Exists(datevPackageDirectory);
			bool directWatchfolderReady = !string.IsNullOrWhiteSpace(datevWatchfolder) && Directory.Exists(datevWatchfolder);
			bool requireDatevXsds = configuration.GetValue("Datev:RequireXsdValidation", defaultValue: true);
			string[] datevXsdPaths = configuration.GetSection("Datev:XsdPaths").Get<string[]>() ?? Array.Empty<string>();
			bool datevXsdsConfigured = !requireDatevXsds || (datevXsdPaths.Length != 0 && datevXsdPaths.All((string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path)));
			string bttnextLogDirectory = configuration["Bttnext:LogDirectory"];
			bool bttnextConfigured = false;
			try
			{
				bttnextConfigured = !string.IsNullOrWhiteSpace(bttnextLogDirectory) && Directory.Exists(bttnextLogDirectory);
				if (bttnextConfigured) _ = Directory.EnumerateFiles(bttnextLogDirectory, "*.log", SearchOption.TopDirectoryOnly).Take(1).ToArray();
			}
			catch { bttnextConfigured = false; }
			string backupDirectory = configuration["Backup:Directory"];
			bool backupConfigured = !string.IsNullOrWhiteSpace(backupDirectory) && Path.IsPathFullyQualified(backupDirectory) && Directory.Exists(backupDirectory);
			bool transferAgentEnabled = configuration.GetValue("Datev:TransferAgentEnabled", defaultValue: false);
			string transferMode = configuration["Datev:TransferMode"] ?? "Disabled";
			string bridgeRoot = configuration["Datev:Bridge:Root"];
			bool bridgeHeartbeatFresh = false;
			string bridgeStatus = transferAgentEnabled ? "heartbeat-missing-or-stale" : "disabled";
			if (transferAgentEnabled && string.Equals(transferMode, "LocalBridge", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(bridgeRoot) && Path.IsPathFullyQualified(bridgeRoot))
			{
				try
				{
					var heartbeatPath = Path.Combine(bridgeRoot, "heartbeat.json");
					var heartbeat = JsonSerializer.Deserialize<NovaNein.Datev.DatevBridgeHeartbeat>(
						await File.ReadAllTextAsync(heartbeatPath, ct), NovaNein.Datev.DatevBridgeJson.SerializerOptions);
					bridgeHeartbeatFresh = heartbeat?.Version == 1 && DateTimeOffset.UtcNow - heartbeat.OccurredAt <= TimeSpan.FromMinutes(2);
					bridgeStatus = !bridgeHeartbeatFresh ? "heartbeat-missing-or-stale" : heartbeat!.Status;
				}
				catch { bridgeStatus = "heartbeat-missing-or-stale"; }
			}
			bool bridgeConfigured = !transferAgentEnabled || (string.Equals(transferMode, "LocalBridge", StringComparison.OrdinalIgnoreCase) && bridgeHeartbeatFresh);
			string paperlessBaseUrl = configuration["Integrations:Paperless:BaseUrl"];
			bool paperlessEnabled = configuration.GetValue("Integrations:Paperless:Enabled", defaultValue: false);
			string paperlessCredentialTarget = configuration["Integrations:Paperless:CredentialTarget"] ?? "NovaNein/Paperless";
			Uri paperlessUri;
			bool paperlessConfigured = !paperlessEnabled || (Uri.TryCreate(paperlessBaseUrl, UriKind.Absolute, out paperlessUri) && (string.Equals(paperlessUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || (string.Equals(paperlessUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && PaperlessClient.IsPrivateHttpHost(paperlessUri.Host))) && WindowsCredentialManager.HasCredential(paperlessCredentialTarget));
			bool allowDirectTransfer = configuration.GetValue("Datev:AllowDirectTransfer", defaultValue: false);
			bool datevConfigured = packageDirectoryReady
				&& (allowDirectTransfer ? directWatchfolderReady : !transferAgentEnabled || (string.Equals(transferMode, "LocalBridge", StringComparison.OrdinalIgnoreCase) && bridgeHeartbeatFresh));
			string sapStatus = (sapConfigured ? "checking" : "not-configured");
			if (sapConfigured)
			{
				try
				{
					await sapClient.CheckReadinessAsync(ct);
					sapStatus = "ok";
				}
				catch (Exception exception) when (IsSapReadFailure(exception))
				{
					loggerFactory.CreateLogger("NovaNein.Server.Health").LogWarning(exception, "Der konfigurierte SAP-Lesezugang ist für den Healthcheck nicht lesbar.");
					sapStatus = "error";
				}
			}
			string attachmentStatus = (attachmentProcessor.AutoAttachEnabled() ? "enabled" : "disabled");
			return Results.Ok(new
			{
				status = ((sapStatus == "ok" && openAiConfigured && storageReady && datevConfigured && datevXsdsConfigured && bttnextConfigured && backupConfigured && bridgeConfigured && paperlessConfigured) ? "ready" : "degraded"),
				sap = sapStatus,
				openAi = (openAiConfigured ? "configured" : "not-configured"),
				storage = (storageReady ? "ok" : "error"),
				datev = (datevConfigured ? "configured" : "not-configured-or-unreachable"),
				datevXsds = (datevXsdsConfigured ? "configured" : "required-before-package-generation"),
				bttnext = (bttnextConfigured ? "configured" : "not-configured-or-unreachable"),
				sapAttachment = attachmentStatus,
				backup = (backupConfigured ? "configured" : "not-configured-or-unreachable"),
				bridgeStatus = ((!bridgeConfigured) ? "enabled-but-not-ready" : (transferAgentEnabled ? "configured" : "disabled")),
				datevBridge = bridgeStatus,
				paperless = (!paperlessEnabled ? "disabled" : (paperlessConfigured ? "configured" : "not-configured-or-unsafe"))
			});
		}).AllowAnonymous();
		app.MapPost("/api/v1/client-health", (Func<ClientHealthReport, HttpRequest, WorkstationRegistry, IConfiguration, ReminderStore, CancellationToken, Task<IResult>>)async delegate(ClientHealthReport report, HttpRequest request, WorkstationRegistry workstations, IConfiguration configuration, ReminderStore reminders, CancellationToken ct)
		{
			string thumbprint = request.HttpContext.Connection.ClientCertificate?.Thumbprint;
			if (string.IsNullOrWhiteSpace(thumbprint))
			{
				return Results.Unauthorized();
			}
			try
			{
				WorkstationHealthSnapshot workstation = await workstations.RecordHealthAsync(thumbprint, report, ct);
				await reminders.EnsureDefaultAsync(thumbprint, ct);
				string serverVersion = configuration["Product:Version"] ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
				string minimumClientVersion = configuration["Product:MinimumClientVersion"] ?? "1.1.0";
				return Results.Ok(new
				{
					serverVersion = serverVersion,
					compatible = ClientHealthRules.IsCompatible(workstation.ClientVersion, minimumClientVersion),
					checkedAt = workstation.LastSeenAt,
					workstation = workstation
				});
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
		});
		app.MapGet("/api/v1/client-health/history", (Func<int?, HttpRequest, WorkstationRegistry, CancellationToken, Task<IResult>>)async delegate(int? limit, HttpRequest request, WorkstationRegistry workstations, CancellationToken ct)
		{
			string thumbprint = request.HttpContext.Connection.ClientCertificate?.Thumbprint;
			if (string.IsNullOrWhiteSpace(thumbprint))
			{
				return Results.Unauthorized();
			}
			try
			{
				return Results.Ok(await workstations.HealthHistoryAsync(thumbprint, limit ?? 20, ct));
			}
			catch (ArgumentOutOfRangeException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
		});
		app.MapGet("/api/v1/statistics/summary", (Func<HttpRequest, DocumentStore, CancellationToken, Task<IResult>>)async delegate(HttpRequest request, DocumentStore documents, CancellationToken ct)
		{
			string thumbprint = request.HttpContext.Connection.ClientCertificate?.Thumbprint;
			return (!string.IsNullOrWhiteSpace(thumbprint)) ? Results.Ok(await documents.StatisticsAsync(ct)) : Results.Unauthorized();
		});
		app.MapGet("/api/v1/statistics/overview", (Func<BusinessStatisticsService, CancellationToken, Task<IResult>>)async delegate(BusinessStatisticsService statistics, CancellationToken ct)
		{
			try
			{
				return Results.Ok(await statistics.GetOverviewAsync(ct));
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				return Results.Problem("SAP ist für die Umsatzstatistik vorübergehend nicht erreichbar.", null, 503);
			}
		}).RequireAuthorization("Accounting.View");
		app.MapPost("/api/v1/documents/incoming", (Func<HttpRequest, IncomingDocumentIntake, DocumentStore, ISapServiceLayerClient, PdfUploadStore, PdfStorageCoordinator, CancellationToken, Task<IResult>>)async delegate(HttpRequest request, IncomingDocumentIntake intake, DocumentStore documents, ISapServiceLayerClient sap, PdfUploadStore uploads, PdfStorageCoordinator storageCoordinator, CancellationToken ct)
		{
			if (!request.HasFormContentType)
			{
				return Results.BadRequest(new
				{
					error = "multipart/form-data mit PDF und SAP-Belegidentität ist erforderlich."
				});
			}
			IFormCollection form = await request.ReadFormAsync(ct);
			if (!int.TryParse(form["docEntry"], out var docEntry) || !int.TryParse(form["docNum"], out var docNum))
			{
				return Results.BadRequest(new
				{
					error = "docEntry und docNum müssen gültige Ganzzahlen sein."
				});
			}
			SapDocumentSnapshot sapDocument;
			try
			{
				sapDocument = await sap.GetDocumentAsync(SapDocumentKind.PurchaseInvoice, docEntry, ct);
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				return Results.Problem("Der konfigurierte SAP-Lesezugang ist nicht verfügbar; das PDF wurde nicht übernommen.", null, 503);
			}
			if (sapDocument.DocNum != docNum)
			{
				return Results.Conflict(new
				{
					error = "Die übermittelte DocNum stimmt nicht mit dem aus SAP gelesenen Beleg überein."
				});
			}
			IFormFile pdf = form.Files.GetFile("pdf");
			if (pdf == null)
			{
				return Results.BadRequest(new
				{
					error = "Genau eine nicht-leere PDF ist erforderlich."
				});
			}
			IResult result;
			await using (await storageCoordinator.EnterAsync(ct))
			{
				if ((object)(await documents.GetBySapAsync(DocumentDirection.Incoming, sapDocument.DocEntry, ct)) != null)
				{
					await intake.ReconcileAsync(ct);
					result = Results.Conflict(new
					{
						error = "Dieser SAP-Beleg wurde bereits verarbeitet; es wurde keine weitere PDF gespeichert."
					});
				}
				else
				{
					PdfUploadStoreResult stored;
					try
					{
						await using Stream input = pdf.OpenReadStream();
						stored = await uploads.StoreAsync(pdf.FileName, pdf.Length, input, ct);
					}
					catch (PdfUploadTooLargeException ex)
					{
						result = Results.Json(new
						{
							error = ex.Message
						}, (JsonSerializerOptions?)null, (string?)null, (int?)413);
						goto end_IL_033e;
					}
					catch (InvalidPdfUploadException ex2)
					{
						result = Results.BadRequest(new
						{
							error = ex2.Message
						});
						goto end_IL_033e;
					}
					try
					{
						DocumentRecord item = await intake.AcceptAsync(new SapDocumentIdentity(DocumentDirection.Incoming, sapDocument.DocEntry, sapDocument.DocNum, SapBusinessDocumentType.PurchaseInvoice), stored.Sha256, Path.GetFileName(pdf.FileName), Actor(request), ct);
						result = Results.Created($"/api/v1/documents/{item.Id}", item);
					}
					catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
					{
						result = Results.Conflict(new
						{
							error = "PDF oder SAP-Beleg wurde bereits verarbeitet; keine Dublette erzeugt."
						});
					}
				}
				end_IL_033e:;
			}
			return result;
		}).RequireAuthorization("Documents.Review");
		app.MapPost("/api/v1/documents/outgoing/{docEntry:int}/generate", (Func<int, HttpRequest, OutgoingDocumentIntake, DocumentStore, ISapServiceLayerClient, PdfUploadStore, PdfStorageCoordinator, CancellationToken, Task<IResult>>)async delegate(int docEntry, HttpRequest request, OutgoingDocumentIntake intake, DocumentStore documents, ISapServiceLayerClient sap, PdfUploadStore uploads, PdfStorageCoordinator storageCoordinator, CancellationToken ct)
		{
			if (!request.HasFormContentType)
			{
				return Results.BadRequest(new
				{
					error = "multipart/form-data mit Coresuite-PDF und SAP-DocNum ist erforderlich."
				});
			}
			IFormCollection form = await request.ReadFormAsync(ct);
			if (!int.TryParse(form["docNum"], out var docNum))
			{
				return Results.BadRequest(new
				{
					error = "docNum muss eine gültige Ganzzahl sein."
				});
			}
			SapDocumentSnapshot sapDocument;
			try
			{
				sapDocument = await sap.GetDocumentAsync(SapDocumentKind.Invoice, docEntry, ct);
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				return Results.Problem("Der konfigurierte SAP-Lesezugang ist nicht verfügbar; die Coresuite-PDF wurde nicht übernommen.", null, 503);
			}
			if (sapDocument.DocNum != docNum)
			{
				return Results.Conflict(new
				{
					error = "Die übermittelte DocNum stimmt nicht mit dem aus SAP gelesenen Ausgangsbeleg überein."
				});
			}
			IFormFile pdf = form.Files.GetFile("pdf");
			if (pdf == null)
			{
				return Results.BadRequest(new
				{
					error = "Genau eine nicht-leere Coresuite-PDF ist erforderlich."
				});
			}
			IResult result;
			await using (await storageCoordinator.EnterAsync(ct))
			{
				if ((object)(await documents.GetBySapAsync(DocumentDirection.Outgoing, sapDocument.DocEntry, ct)) != null)
				{
					await intake.ReconcileAsync(ct);
					result = Results.Conflict(new
					{
						error = "Dieser SAP-Ausgangsbeleg wurde bereits verarbeitet; es wurde keine weitere PDF gespeichert."
					});
				}
				else
				{
					PdfUploadStoreResult stored;
					try
					{
						await using Stream input = pdf.OpenReadStream();
						stored = await uploads.StoreAsync(pdf.FileName, pdf.Length, input, ct);
					}
					catch (PdfUploadTooLargeException ex)
					{
						result = Results.Json(new
						{
							error = ex.Message
						}, (JsonSerializerOptions?)null, (string?)null, (int?)413);
						goto end_IL_0322;
					}
					catch (InvalidPdfUploadException ex2)
					{
						result = Results.BadRequest(new
						{
							error = ex2.Message
						});
						goto end_IL_0322;
					}
					try
					{
						DocumentRecord item = await intake.AcceptAsync(new SapDocumentIdentity(DocumentDirection.Outgoing, sapDocument.DocEntry, sapDocument.DocNum, SapBusinessDocumentType.Invoice), stored.Sha256, Path.GetFileName(pdf.FileName), Actor(request), ct);
						result = Results.Created($"/api/v1/documents/{item.Id}", item);
					}
					catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
					{
						result = Results.Conflict(new
						{
							error = "PDF oder SAP-Ausgangsbeleg wurde bereits verarbeitet; keine Dublette erzeugt."
						});
					}
				}
				end_IL_0322:;
			}
			return result;
		}).RequireAuthorization("Documents.Review");
		app.MapGet("/api/v1/documents/{id:guid}", (Func<Guid, DocumentStore, CancellationToken, Task<IResult>>)async delegate(Guid id, DocumentStore store, CancellationToken ct)
		{
			DocumentRecord item = await store.GetAsync(id, ct);
			return ((object)item != null) ? Results.Ok(item) : Results.NotFound();
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/documents/{id:guid}/events", (Func<Guid, DocumentStore, CancellationToken, Task<IResult>>)(async (Guid id, DocumentStore store, CancellationToken ct) => ((object)(await store.GetAsync(id, ct)) != null) ? Results.Ok(await store.EventsAsync(id, ct)) : Results.NotFound())).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/activity", (Func<int?, DocumentStore, CancellationToken, Task<IResult>>)(async (int? limit, DocumentStore store, CancellationToken ct) => Results.Ok(await store.RecentActivityAsync(limit ?? 50, ct)))).RequireAuthorization("Audit.View");
		app.MapGet("/api/v1/documents/{id:guid}/datev", (Func<Guid, DocumentStore, TransferEvidenceStore, DatevTransferRequestStore, DocumentJobQueue, IConfiguration, CancellationToken, Task<IResult>>)async delegate(Guid id, DocumentStore documents, TransferEvidenceStore evidence, DatevTransferRequestStore transferRequests, DocumentJobQueue documentJobs, IConfiguration configuration, CancellationToken ct)
		{
			DocumentRecord item = await documents.GetAsync(id, ct);
			if ((object)item == null)
			{
				return Results.NotFound();
			}
			TransferEvidence transfer = await evidence.GetAsync(id, ct);
			DatevTransferRequest transferRequest = await transferRequests.GetByDocumentAsync(id, ct);
			string pdfRoot = Path.GetFullPath(configuration["Storage:DocumentRoot"] ?? "data/documents");
			string pdfPath = Path.Combine(pdfRoot, item.PdfSha256 + ".pdf");
			bool pdfArchived = File.Exists(pdfPath);
			DateTimeOffset? packagePreparedAt = transfer?.PackagePreparedAt;
			string packageFileName = transfer?.PackageFileName;
			string packageSha = transfer?.PackageSha256;
			DateTimeOffset? uploadSucceededAt = transfer?.UploadSucceededAt;
			DateTimeOffset? jobFinalizedAt = transfer?.JobFinalizedAt;
			bool transferred = transfer?.IsTransferred ?? false;
			DocumentJob packageJob = await documentJobs.GetAsync(id, DocumentJobKind.CreateDatevPackage, ct);
			return Results.Ok(new
			{
				documentId = id,
				pdfArchived = pdfArchived,
				packagePreparedAt = packagePreparedAt,
				packageFileName = packageFileName,
				packageSha256 = packageSha,
				uploadSucceededAt = uploadSucceededAt,
				jobFinalizedAt = jobFinalizedAt,
				transferred = transferred,
				documentStatus = item.Status.ToString(),
				packageJobStatus = packageJob?.State.ToString(),
				packageJobError = packageJob?.State == DocumentJobState.Failed ? packageJob.LastError : null,
				transferState = transferRequest?.Status,
				transferAttempts = transferRequest?.Attempts ?? 0,
				transferError = transferRequest?.LastError,
				bridgeStagedAt = transferRequest?.BridgeStagedAt,
				watchfolderDeliveredAt = transferRequest?.WatchfolderDeliveredAt,
				bttnextWaiting = transferRequest?.Status == "awaiting-datev-confirmation" && transferRequest.WatchfolderDeliveredAt.HasValue
					&& DateTimeOffset.UtcNow - transferRequest.WatchfolderDeliveredAt.Value >= TimeSpan.FromMinutes(15),
				transferRequest = transferRequest
			});
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/documents/{id:guid}/datev/package", async (Guid id, HttpContext context, TransferEvidenceStore evidence, IConfiguration configuration, CancellationToken ct) =>
		{
			if (!WebAuthorization.HasReviewerAccess(context.User)) return Results.Forbid();
			var transfer = await evidence.GetAsync(id, ct);
			if (transfer is null || string.IsNullOrWhiteSpace(transfer.PackageFileName)) return Results.NotFound();
			var configuredRoot = configuration["Datev:PackageDirectory"];
			if (string.IsNullOrWhiteSpace(configuredRoot)) return Results.Problem("DATEV-Paketverzeichnis ist nicht konfiguriert.", statusCode: 503);
			var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
			var prefix = root + Path.DirectorySeparatorChar;
			var packagePath = Directory.EnumerateFiles(root, transfer.PackageFileName, SearchOption.AllDirectories)
				.Select(Path.GetFullPath)
				.FirstOrDefault(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
			if (packagePath is null) return Results.NotFound();
			await using (var stream = File.OpenRead(packagePath))
			{
				if (!string.Equals(DatevPackageRules.Sha256(stream), transfer.PackageSha256, StringComparison.OrdinalIgnoreCase))
					return Results.Problem("Die DATEV-ZIP-Prüfsumme stimmt nicht mit dem Paketnachweis überein.", statusCode: 409);
			}
			return Results.File(packagePath, "application/zip", transfer.PackageFileName, enableRangeProcessing: false);
		}).RequireAuthorization("Documents.Review");
		app.MapPost("/api/v1/documents/{id:guid}/transfer-requests", (Func<Guid, DatevTransferRequestRequest, HttpContext, DatevTransferRequestStore, CancellationToken, Task<IResult>>)async delegate(Guid id, DatevTransferRequestRequest request, HttpContext context, DatevTransferRequestStore transferRequests, CancellationToken ct)
		{
			if (!WebAuthorization.HasReviewerAccess(context.User))
			{
				return Results.Forbid();
			}
			if (!request.Confirm)
			{
				return Results.BadRequest(new
				{
					error = "Die Übertragung muss ausdrücklich bestätigt werden."
				});
			}
			try
			{
				string actor = Actor(context.Request);
				DatevTransferRequest result = await transferRequests.RequestAsync(id, request.PackageSha256, actor, ct);
				return Results.Accepted($"/api/v1/documents/{id:D}/datev", result);
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
			catch (InvalidOperationException ex2)
			{
				return Results.Conflict(new
				{
					error = ex2.Message
				});
			}
		}).RequireAuthorization("Documents.Review");
		Func<Guid, HttpContext, DocumentStore, TransferEvidenceStore, DocumentJobQueue, ISapServiceLayerClient, CancellationToken, Task<IResult>> prepareDatevPackage = async (id, context, documents, evidence, documentJobs, sap, ct) =>
		{
			if (!WebAuthorization.HasReviewerAccess(context.User)) return Results.Forbid();
			var item = await documents.GetAsync(id, ct);
			if (item is null) return Results.NotFound();
			var existing = await evidence.GetAsync(id, ct);
			if (existing is not null)
			{
				return Results.Ok(new { documentId = id, status = DocumentStatus.Packaged.ToString(), packageFileName = existing.PackageFileName, packageSha256 = existing.PackageSha256 });
			}
			var currentJob = await documentJobs.GetAsync(id, DocumentJobKind.CreateDatevPackage, ct);
			var isSafePackageRetry = currentJob?.State is DocumentJobState.Failed or DocumentJobState.Completed
				|| (currentJob?.State == DocumentJobState.Queued && !string.IsNullOrWhiteSpace(currentJob.LastError));
			if (!DocumentWorkflow.MayCreateDatevPackage(item) && !isSafePackageRetry)
				return Results.Conflict(new { error = "Das DATEV-Paket darf erst nach der fachlichen Freigabe vorbereitet werden." });
			if (item.Sap.Type.IsCreditNote() && !await documents.HasCreditNoteDatevReleaseAsync(id, ct))
				return Results.Conflict(new { error = "Die Gutschrift muss ausdrücklich und begründet für DATEV freigegeben werden." });
			if (!item.Sap.Type.IsInvoice() && !item.Sap.Type.IsCreditNote())
				return Results.Conflict(new { error = "Dieser SAP-Belegtyp wird für DATEV nicht unterstützt." });
			SapAccountingDocument? accounting;
			try
			{
				accounting = await sap.GetAccountingDocumentAsync(item.Sap.Type.ToServer(item.Sap.Direction), item.Sap.DocEntry, ct);
			}
			catch (Exception) when (!ct.IsCancellationRequested)
			{
				return Results.Conflict(new { error = "Die vollständigen SAP-Buchungsdaten konnten nicht sicher gelesen werden; das DATEV-Paket wurde nicht eingereiht." });
			}
			if (accounting is null || !accounting.IsComplete)
				return Results.Conflict(new
				{
					error = "Die SAP-Buchungsdaten sind unvollständig oder widersprüchlich; das DATEV-Paket wurde nicht eingereiht.",
					details = accounting?.CompletenessIssues ?? new[] { "SAP-Buchungsdaten fehlen." }
				});
			if (isSafePackageRetry)
			{
				if (!await documentJobs.RetryDatevPackageAsync(id, Actor(context.Request), ct))
					return Results.Conflict(new { error = "Der fehlgeschlagene DATEV-Paketjob konnte nicht sicher wiederholt werden." });
			}
			else
			{
				await documentJobs.EnsureEnqueuedAsync(id, DocumentJobKind.CreateDatevPackage, ct);
			}
			return Results.Accepted($"/api/v1/documents/{id:D}/datev", new { documentId = id, status = item.Status.ToString(), nextAction = "DATEV-Paket wird automatisch vorbereitet" });
		};
		app.MapPost("/api/v1/documents/{id:guid}/datev/package", prepareDatevPackage).RequireAuthorization("Documents.Review");
		app.MapPost("/api/v1/documents/{id:guid}/datev/prepare", prepareDatevPackage).RequireAuthorization("Documents.Review");

		app.MapPost("/api/v1/documents/{id:guid}/credit-note-release", (Func<Guid, ReviewRequest, HttpRequest, DocumentStore, DocumentJobQueue, ISapServiceLayerClient, CancellationToken, Task<IResult>>)async delegate(Guid id, ReviewRequest release, HttpRequest request, DocumentStore store, DocumentJobQueue documentJobQueue, ISapServiceLayerClient sap, CancellationToken ct)
		{
			if (!WebAuthorization.HasReviewerAccess(request.HttpContext.User)) return Results.Forbid();
			if (!release.Approve) return Results.BadRequest(new { error = "Die Gutschrift muss ausdrücklich für DATEV freigegeben werden." });
			if (string.IsNullOrWhiteSpace(release.Reason)) return Results.BadRequest(new { error = "Für die DATEV-Freigabe ist eine Begründung erforderlich." });

			var item = await store.GetAsync(id, ct);
			if (item is null) return Results.NotFound();
			if (!item.Sap.Type.IsCreditNote()) return Results.Conflict(new { error = "Der Beleg ist keine Gutschrift." });
			if (!DocumentWorkflow.MayCreateDatevPackage(item) && item.Status is not (DocumentStatus.Packaged or DocumentStatus.Transferred))
				return Results.Conflict(new { error = "Die Gutschrift muss zuerst fachlich geprüft und freigegeben werden." });

			SapAccountingDocument? accounting;
			try
			{
				accounting = await sap.GetAccountingDocumentAsync(item.Sap.Type.ToServer(item.Sap.Direction), item.Sap.DocEntry, ct);
			}
			catch (Exception) when (!ct.IsCancellationRequested)
			{
				return Results.Conflict(new { error = "Die vollständigen SAP-Buchungsdaten konnten nicht sicher gelesen werden; die Gutschrift wurde nicht für DATEV freigegeben." });
			}
			if (accounting is null || !accounting.IsComplete)
				return Results.Conflict(new
				{
					error = "Die SAP-Buchungsdaten sind unvollständig oder widersprüchlich; die Gutschrift wurde nicht für DATEV freigegeben.",
					details = accounting?.CompletenessIssues ?? new[] { "SAP-Buchungsdaten fehlen." }
				});

			if (!await store.RecordCreditNoteDatevReleaseAsync(id, release.Reason, Actor(request), ct))
				return Results.Conflict(new { error = "Die Gutschrift konnte in ihrem aktuellen Status nicht für DATEV freigegeben werden." });
			await documentJobQueue.EnsureEnqueuedAsync(id, DocumentJobKind.CreateDatevPackage, ct);
			return Results.Accepted($"/api/v1/documents/{id:D}/datev", new { documentId = id, status = item.Status.ToString(), nextAction = "DATEV-Paket wird vorbereitet" });
		}).RequireAuthorization("Documents.Review");

		app.MapPost("/api/v1/documents/{id:guid}/transfer-requests/retry", (Func<Guid, HttpContext, DatevTransferRequestStore, CancellationToken, Task<IResult>>)async delegate(Guid id, HttpContext context, DatevTransferRequestStore transferRequests, CancellationToken ct)
		{
			if (!WebAuthorization.HasReviewerAccess(context.User))
			{
				return Results.Forbid();
			}
			try
			{
				DatevTransferRequest result = await transferRequests.RetryAsync(id, Actor(context.Request), ct);
				return ((object)result == null) ? Results.Conflict(new
				{
					error = "Für diesen Beleg gibt es keinen fehlgeschlagenen DATEV-Transferauftrag."
				}) : Results.Accepted($"/api/v1/documents/{id:D}/datev", result);
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
		}).RequireAuthorization("Documents.Review");
		app.MapGet("/api/v1/documents/by-sap/{direction}/{docEntry:int}/pdf", (Func<string, int, string, DocumentStore, IConfiguration, CancellationToken, Task<IResult>>)async delegate(string direction, int docEntry, string? sapKind, DocumentStore store, IConfiguration configuration, CancellationToken ct)
		{
			string text = direction.ToLowerInvariant();
			DocumentDirection? documentDirection = ((text == "incoming") ? new DocumentDirection?(DocumentDirection.Incoming) : ((!(text == "outgoing")) ? ((DocumentDirection?)null) : new DocumentDirection?(DocumentDirection.Outgoing)));
			DocumentDirection? parsed = documentDirection;
			if (!parsed.HasValue)
			{
				return Results.BadRequest(new
				{
					error = "Richtung muss incoming oder outgoing sein."
				});
			}
			SapDocumentKind parsedKind;
			DocumentRecord documentRecord = ((string.IsNullOrWhiteSpace(sapKind) || !TryParseSapDocumentKind(sapKind, out parsedKind)) ? (await store.GetBySapAsync(parsed.Value, docEntry, ct)) : (await store.GetBySapAsync(parsed.Value, parsedKind.ToDomain(), docEntry, ct)));
			DocumentRecord item = documentRecord;
			if ((object)item == null)
			{
				return Results.NotFound();
			}
			string root = Path.GetFullPath(configuration["Storage:DocumentRoot"] ?? "data/documents");
			string rootPrefix = (root.EndsWith(Path.DirectorySeparatorChar) ? root : (root + Path.DirectorySeparatorChar));
			string path = Path.GetFullPath(Path.Combine(root, item.PdfSha256 + ".pdf"));
			return (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) ? Results.NotFound() : Results.File(path, "application/pdf", null, null, null, enableRangeProcessing: true);
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/documents/by-sap/{direction}/{docEntry:int}", (Func<string, int, string, DocumentStore, CancellationToken, Task<IResult>>)async delegate(string direction, int docEntry, string? sapKind, DocumentStore store, CancellationToken ct)
		{
			string text = direction.ToLowerInvariant();
			DocumentDirection? documentDirection = ((text == "incoming") ? new DocumentDirection?(DocumentDirection.Incoming) : ((!(text == "outgoing")) ? ((DocumentDirection?)null) : new DocumentDirection?(DocumentDirection.Outgoing)));
			DocumentDirection? parsed = documentDirection;
			if (!parsed.HasValue)
			{
				return Results.BadRequest(new
				{
					error = "Richtung muss incoming oder outgoing sein."
				});
			}
			SapDocumentKind parsedKind;
			DocumentRecord documentRecord = ((string.IsNullOrWhiteSpace(sapKind) || !TryParseSapDocumentKind(sapKind, out parsedKind)) ? (await store.GetBySapAsync(parsed.Value, docEntry, ct)) : (await store.GetBySapAsync(parsed.Value, parsedKind.ToDomain(), docEntry, ct)));
			DocumentRecord item = documentRecord;
			return ((object)item != null) ? Results.Ok(item) : Results.NotFound();
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/scans/missing-pdf", (Func<DateOnly?, DateOnly?, ISapServiceLayerClient, ILoggerFactory, CancellationToken, Task<IResult>>)async delegate(DateOnly? fromEntryDate, DateOnly? toEntryDate, ISapServiceLayerClient sap, ILoggerFactory loggerFactory, CancellationToken ct)
		{
			DateOnly today = DateOnly.FromDateTime(DateTime.Today);
			DateOnly end = toEntryDate ?? today;
			DateOnly start = fromEntryDate ?? end.AddDays(-7);
			try
			{
				return Results.Ok((await sap.FindMissingPdfAttachmentsAsync(start, end, ct)).Select((SapAttachmentGap item) => new
				{
					kind = item.Kind.ToString(),
					DocEntry = item.DocEntry,
					DocNum = item.DocNum,
					entryDate = item.EntryDate.ToString("yyyy-MM-dd"),
					AttachmentEntry = item.AttachmentEntry
				}));
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				loggerFactory.CreateLogger("NovaNein.Server.SapScan").LogWarning(exception, "SAP PDF-Anhangscan konnte nicht ausgeführt werden.");
				return Results.Problem("Der konfigurierte SAP-Lesezugang ist für den PDF-Anhangscan nicht verfügbar.", null, 503);
			}
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/sap/documents/{kind}/{docEntry:int}", (Func<string, int, ISapServiceLayerClient, ILoggerFactory, CancellationToken, Task<IResult>>)async delegate(string kind, int docEntry, ISapServiceLayerClient sap, ILoggerFactory loggerFactory, CancellationToken ct)
		{
			if (!TryParseSapDocumentKind(kind, out var parsedKind))
			{
				return Results.BadRequest(new
				{
					error = "Unbekannte SAP-Belegart."
				});
			}
			try
			{
				return Results.Ok(await sap.GetDocumentAsync(parsedKind, docEntry, ct));
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				loggerFactory.CreateLogger("NovaNein.Server.SapCorpus").LogWarning(exception, "SAP-Korpusbeleg {Kind}/{DocEntry} konnte nicht gelesen werden.", kind, docEntry);
				return Results.Problem("Der konfigurierte SAP-Lesezugang ist nicht verfügbar.", null, 503);
			}
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/sap/documents/{kind}/{docEntry:int}/datev-readiness", (Func<string, int, HttpContext, ISapServiceLayerClient, IConfiguration, ILoggerFactory, CancellationToken, Task<IResult>>)async delegate(string kind, int docEntry, HttpContext context, ISapServiceLayerClient sap, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct)
		{
			if (!WebAuthorization.HasReviewerAccess(context.User)) return Results.Forbid();
			if (!TryParseSapDocumentKind(kind, out var parsedKind))
			{
				return Results.BadRequest(new { error = "Unbekannte SAP-Belegart." });
			}
			try
			{
				var accounting = await sap.GetAccountingDocumentAsync(parsedKind, docEntry, ct);
				if (accounting is null) return Results.NotFound();
				var rawSapMappings = new List<object>();
				var sqlConnectionString = configuration["Sap:Sql:ConnectionString"];
				if (!string.IsNullOrWhiteSpace(sqlConnectionString))
				{
					await using var connection = new SqlConnection(SqlSapReadClient.BuildReadOnlyConnectionString(sqlConnectionString));
					await connection.OpenAsync(ct);
					var taxCodes = accounting.Lines.Select(line => line.TaxCode).Concat(accounting.Taxes.Select(tax => tax.TaxCode)).Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.OrdinalIgnoreCase);
					foreach (var taxCode in taxCodes)
					{
						await using var command = connection.CreateCommand();
						command.CommandText = "SELECT [Code], [EffecDate], [Rate], [DatevCode], [LogInstanc] FROM [dbo].[AVT1] WHERE [Code] = @code ORDER BY [EffecDate] DESC, [LogInstanc] DESC";
						command.Parameters.AddWithValue("@code", taxCode);
						await using var reader = await command.ExecuteReaderAsync(ct);
						while (await reader.ReadAsync(ct))
						{
							rawSapMappings.Add(new
							{
								code = reader.GetString(0),
								effectiveDate = reader.GetDateTime(1),
								rate = reader.GetDecimal(2),
								datevCode = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
								logInstance = reader.GetInt32(4)
							});
						}
					}
				}
				return Results.Ok(new
				{
					accounting.Snapshot.DocEntry,
					accounting.Snapshot.DocNum,
					accounting.Snapshot.DocumentDate,
					accounting.Snapshot.GrossAmount,
					accounting.Snapshot.Currency,
					accounting.IsComplete,
					accounting.CompletenessIssues,
					lines = accounting.Lines.Select(line => new { line.LineNum, line.Account, line.TaxCode, line.TaxRate, line.NetAmount, line.TaxAmount, line.IsReverseCharge }),
					taxes = accounting.Taxes.Select(tax => new { tax.LineNum, tax.TaxCode, tax.Rate, tax.NetAmount, tax.TaxAmount, tax.TaxAccount, tax.ReverseChargePercent, tax.ReverseChargeTaxAmount, tax.IncludedInGrossRevenue, tax.IsReverseCharge }),
					journal = accounting.JournalLines.Select(line => new { line.LineId, line.Account, line.CounterAccount, line.DebitCredit, line.Debit, line.Credit, line.Currency }),
					datevMappings = accounting.DatevMappings.Select(mapping => new { mapping.SapTaxCode, mapping.DatevBuCode, mapping.DatevAccount, mapping.ValidFrom, mapping.ValidTo, mapping.ApprovedBy }),
					rawSapMappings
				});
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				loggerFactory.CreateLogger("NovaNein.Server.DatevReadiness").LogWarning(exception, "SAP-DATEV-Bereitschaft für {Kind}/{DocEntry} konnte nicht gelesen werden.", kind, docEntry);
				return Results.Problem("Der konfigurierte SAP-Lesezugang ist nicht verfügbar.", null, 503);
			}
		}).RequireAuthorization("Documents.View");
		app.MapGet("/api/v1/sap/documents/{kind}/by-doc-num/{docNum:int}", (Func<string, int, ISapServiceLayerClient, ILoggerFactory, CancellationToken, Task<IResult>>)async delegate(string kind, int docNum, ISapServiceLayerClient sap, ILoggerFactory loggerFactory, CancellationToken ct)
		{
			if (!TryParseSapDocumentKind(kind, out var parsedKind))
			{
				return Results.BadRequest(new
				{
					error = "Unbekannte SAP-Belegart."
				});
			}
			try
			{
				SapDocumentSnapshot document3 = await sap.FindDocumentByDocNumAsync(parsedKind, docNum, ct);
				return ((object)document3 == null) ? Results.NotFound() : Results.Ok(document3);
			}
			catch (Exception exception) when (IsSapReadFailure(exception))
			{
				loggerFactory.CreateLogger("NovaNein.Server.SapLookup").LogWarning(exception, "SAP-Belegnummer {DocNum} konnte nicht gelesen werden.", docNum);
				return Results.Problem("Der konfigurierte SAP-Lesezugang ist nicht verfügbar.", null, 503);
			}
		}).RequireAuthorization("Documents.View");
		app.MapPut("/api/v1/reminders/weekly", (Func<WeeklyReminderRequest, HttpRequest, ReminderStore, CancellationToken, Task<IResult>>)async delegate(WeeklyReminderRequest request, HttpRequest http, ReminderStore reminders, CancellationToken ct)
		{
			string recipient = http.HttpContext.Connection.ClientCertificate?.Thumbprint;
			if (string.IsNullOrWhiteSpace(recipient))
			{
				return Results.Unauthorized();
			}
			await reminders.SetEnabledAsync(recipient, request.Enabled, ct);
			return Results.NoContent();
		});
		app.MapGet("/api/v1/reminders/weekly", (Func<HttpRequest, ReminderStore, CancellationToken, Task<IResult>>)async delegate(HttpRequest http, ReminderStore reminders, CancellationToken ct)
		{
			string recipient = http.HttpContext.Connection.ClientCertificate?.Thumbprint;
			return (!string.IsNullOrWhiteSpace(recipient)) ? Results.Ok(new
			{
				enabled = await reminders.IsEnabledAsync(recipient, ct)
			}) : Results.Unauthorized();
		});
		app.MapGet("/api/v1/notifications", (Func<HttpRequest, ReminderStore, CancellationToken, Task<IResult>>)async delegate(HttpRequest http, ReminderStore reminders, CancellationToken ct)
		{
			string recipient = http.HttpContext.Connection.ClientCertificate?.Thumbprint;
			if (string.IsNullOrWhiteSpace(recipient))
			{
				return Results.Unauthorized();
			}
			await reminders.EnsureDefaultAsync(recipient, ct);
			return Results.Ok(await reminders.ListAsync(recipient, ct));
		});
		app.MapPost("/api/v1/notifications/{id:long}/read", (Func<long, HttpRequest, ReminderStore, CancellationToken, Task<IResult>>)async delegate(long id, HttpRequest http, ReminderStore reminders, CancellationToken ct)
		{
			string recipient = http.HttpContext.Connection.ClientCertificate?.Thumbprint;
			return string.IsNullOrWhiteSpace(recipient) ? Results.Unauthorized() : ((await reminders.MarkReadAsync(id, recipient, ct)) ? Results.NoContent() : Results.NotFound(new
			{
				error = "Hinweis nicht gefunden oder gehört nicht zu diesem Arbeitsplatz."
			}));
		});
		app.MapPost("/api/v1/documents/{id:guid}/reviews", (Func<Guid, ReviewRequest, HttpRequest, DocumentStore, DocumentJobQueue, IConfiguration, CancellationToken, Task<IResult>>)async delegate(Guid id, ReviewRequest review, HttpRequest request, DocumentStore store, DocumentJobQueue documentJobQueue, IConfiguration configuration, CancellationToken ct)
		{
			if (!WebAuthorization.HasReviewerAccess(request.HttpContext.User))
			{
				return Results.Forbid();
			}
			try
			{
				DocumentRecord item = await store.ReviewAsync(id, review.Approve, review.Reason, Actor(request), ct);
				if ((object)item != null && item.Status == DocumentStatus.Approved)
				{
					if (app.Services.GetRequiredService<SapAttachmentProcessor>().AutoAttachEnabled())
						await documentJobQueue.EnsureEnqueuedAsync(item.Id, DocumentJobKind.AttachToSap, ct);
					if (configuration.GetValue("Datev:AutoPreparePackages", defaultValue: false) && item.Sap.Type.IsInvoice())
						await documentJobQueue.EnsureEnqueuedAsync(item.Id, DocumentJobKind.CreateDatevPackage, ct);
				}
				return ((object)item == null) ? Results.Conflict(new
				{
					error = "Nur gelbe oder rote fachliche Prüfergebnisse können manuell entschieden werden."
				}) : Results.Ok(item);
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
		}).RequireAuthorization("Documents.Review");
		app.MapPost("/api/v1/documents/{id:guid}/retry-validation", (Func<Guid, RetryValidationRequest, HttpRequest, DocumentStore, DocumentJobQueue, CancellationToken, Task<IResult>>)async delegate(Guid id, RetryValidationRequest retry, HttpRequest request, DocumentStore store, DocumentJobQueue documentJobQueue, CancellationToken ct)
		{
			_ = 1;
			try
			{
				if (!(await documentJobQueue.RetryValidationAsync(id, retry.Reason, Actor(request), ct)))
				{
					return Results.Conflict(new
					{
						error = "Nur gelbe, rote oder endgültig fehlgeschlagene Belege mit abgeschlossenem Validierungsjob können erneut geprüft werden."
					});
				}
				string uri = $"/api/v1/documents/{id}";
				return Results.Accepted(uri, await store.GetAsync(id, ct));
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new
				{
					error = ex.Message
				});
			}
		}).RequireAuthorization("Documents.Review");
		app.MapPost("/api/v1/documents/{id:guid}/replacement-pdf", (Func<Guid, HttpRequest, DocumentStore, DocumentJobQueue, PdfUploadStore, PdfStorageCoordinator, CancellationToken, Task<IResult>>)async delegate(Guid id, HttpRequest request, DocumentStore store, DocumentJobQueue documentJobQueue, PdfUploadStore uploads, PdfStorageCoordinator storageCoordinator, CancellationToken ct)
		{
			if (!request.HasFormContentType)
			{
				return Results.BadRequest(new
				{
					error = "multipart/form-data mit genau einer PDF ist erforderlich."
				});
			}
			IFormFile file = (await request.ReadFormAsync(ct)).Files.GetFile("pdf");
			if (file == null)
			{
				return Results.BadRequest(new
				{
					error = "Genau eine PDF-Datei ist erforderlich."
				});
			}
			try
			{
				DocumentRecord? document;
				await using (await storageCoordinator.EnterAsync(ct))
				{
					await using Stream stream = file.OpenReadStream();
					PdfUploadStoreResult stored = await uploads.StoreAsync(file.FileName, file.Length, stream, ct);
					document = await documentJobQueue.ReplacePdfAndRetryValidationAsync(store, id, stored.Sha256, file.FileName, Actor(request), ct);
				}
				return ((object)document == null) ? Results.NotFound(new
				{
					error = "Der Beleg wurde nicht gefunden."
				}) : Results.Accepted($"/api/v1/documents/{id:D}", document);
			}
			catch (PdfUploadTooLargeException ex)
			{
				return Results.Json(new
				{
					error = ex.Message
				}, (JsonSerializerOptions?)null, (string?)null, (int?)413);
			}
			catch (InvalidPdfUploadException ex2)
			{
				return Results.BadRequest(new
				{
					error = ex2.Message
				});
			}
			catch (ArgumentException ex3)
			{
				return Results.BadRequest(new
				{
					error = ex3.Message
				});
			}
			catch (InvalidOperationException ex4)
			{
				return Results.Conflict(new
				{
					error = ex4.Message
				});
			}
			catch (SqliteException ex5) when (ex5.SqliteErrorCode == 19)
			{
				return Results.Conflict(new
				{
					error = "Diese PDF ist bereits einem anderen NovaNein-Beleg zugeordnet."
				});
			}
		}).RequireAuthorization("Documents.Review");
		app.Run();
		static void AddPermissionPolicy(AuthorizationOptions options, string policyName, string permission, params string[] compatibleRoles)
		{
			options.AddPolicy(policyName, policy =>
			{
				policy.RequireAuthenticatedUser().RequireAssertion(context =>
					context.User.IsInRole("Admin") ||
					context.User.IsInRole("Manager") ||
					(string.Equals(context.User.FindFirstValue("novanein:access"), "workstation-certificate", StringComparison.Ordinal) &&
					 compatibleRoles.Any(context.User.IsInRole)) ||
					context.User.HasClaim("novanein:permission", permission));
			});
		}
		static Claim[] CreateUserClaims(WebUser user)
		{
			List<Claim> claims =
			[
				new(ClaimTypes.NameIdentifier, user.Id.ToString()),
				new(ClaimTypes.Name, user.UserName),
				new(ClaimTypes.Email, user.Email),
				new(ClaimTypes.Role, user.Role),
				new("novanein:display-name", user.DisplayName),
				new("novanein:must-change-password", user.MustChangePassword ? "true" : "false"),
				new("novanein:access", "session")
			];
			claims.AddRange(user.Permissions.Select(permission => new Claim("novanein:permission", permission)));
			return claims.ToArray();
		}
		static string Actor(HttpRequest request)
		{
			IIdentity? identity = request.HttpContext.User.Identity;
			if (identity != null && identity.IsAuthenticated && !string.IsNullOrWhiteSpace(request.HttpContext.User.Identity.Name))
			{
				return request.HttpContext.User.Identity.Name;
			}
			return RequestActorFormatter.Format(request.HttpContext.Connection.ClientCertificate?.Thumbprint, request.Headers["X-NovaNein-Sap-User"].FirstOrDefault());
		}
		static string Csv(object? value)
		{
			string text = value?.ToString() ?? "";
			if (text.Length > 0 && "=+-@".Contains(text[0]))
			{
				text = "'" + text;
			}
			return "\"" + text.Replace("\"", "\"\"") + "\"";
		}
		static bool IsSapReadConfigured(IConfiguration configuration)
		{
			if (string.Equals(configuration["Sap:ReadMode"], "sql-read-only", StringComparison.OrdinalIgnoreCase))
			{
				return !string.IsNullOrWhiteSpace(configuration["Sap:Sql:ConnectionString"]);
			}
			if (!string.IsNullOrWhiteSpace(configuration["Sap:Endpoint"]) && !configuration["Sap:Endpoint"].Contains("example.invalid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configuration["Sap:CompanyDatabase"]) && !string.IsNullOrWhiteSpace(configuration["Sap:UserName"]))
			{
				return !string.IsNullOrWhiteSpace(configuration["Sap:Password"]);
			}
			return false;
		}
		static bool IsSapReadFailure(Exception exception)
		{
			if (exception is HttpRequestException || exception is InvalidOperationException || exception is TaskCanceledException || exception is SqlException)
			{
				return true;
			}
			return false;
		}
		static X509Certificate2? LoadServerCertificate(IConfiguration configuration)
		{
			string thumbprint = configuration["Tls:ServerCertificateThumbprint"];
			if (string.IsNullOrWhiteSpace(thumbprint))
			{
				return null;
			}
			string normalized = WorkstationRegistry.NormalizeThumbprint(thumbprint);
			StoreLocation configuredLocation;
			StoreLocation storeLocation = ((!Enum.TryParse<StoreLocation>(configuration["Tls:CertificateStoreLocation"], ignoreCase: true, out configuredLocation)) ? StoreLocation.CurrentUser : configuredLocation);
			using X509Store store = new X509Store(StoreName.My, storeLocation);
			store.Open(OpenFlags.ReadOnly);
			X509Certificate2 certificate = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false).OfType<X509Certificate2>().SingleOrDefault();
			if (certificate == null || !certificate.HasPrivateKey)
			{
				throw new InvalidOperationException("Das konfigurierte TLS-Serverzertifikat wurde nicht mit privatem Schlüssel im CurrentUser-Zertifikatsspeicher gefunden.");
			}
			return certificate;
		}
		static X509Certificate2? LoadTrustedClientRoot(IConfiguration configuration)
		{
			string thumbprint = configuration["Tls:ClientRootCertificateThumbprint"];
			if (string.IsNullOrWhiteSpace(thumbprint))
			{
				return null;
			}
			string normalized = WorkstationRegistry.NormalizeThumbprint(thumbprint);
			using X509Store store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
			store.Open(OpenFlags.ReadOnly);
			return store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false).OfType<X509Certificate2>().SingleOrDefault() ?? throw new InvalidOperationException("Das konfigurierte Client-Root-Zertifikat wurde nicht im LocalMachine-Root-Speicher gefunden.");
		}
		static string ReadConsolePassword()
		{
			StringBuilder builder2 = new StringBuilder();
			while (true)
			{
				ConsoleKeyInfo key = Console.ReadKey(intercept: true);
				if (key.Key == ConsoleKey.Enter)
				{
					break;
				}
				if (key.Key == ConsoleKey.Backspace)
				{
					if (builder2.Length > 0)
					{
						builder2.Length--;
					}
				}
				else if (!char.IsControl(key.KeyChar))
				{
					builder2.Append(key.KeyChar);
				}
			}
			return builder2.ToString();
		}
		static bool TryParseSapDocumentKind(string value, out SapDocumentKind kind)
		{
			SapDocumentKind sapDocumentKind;
			switch (value.Trim().ToLowerInvariant())
			{
			case "purchase-invoice":
			case "eingangsrechnung":
			case "purchaseinvoice":
				sapDocumentKind = SapDocumentKind.PurchaseInvoice;
				break;
			case "outgoing-invoice":
			case "ausgangsrechnung":
			case "invoice":
				sapDocumentKind = SapDocumentKind.Invoice;
				break;
			case "eingangsgutschrift":
			case "purchasecreditnote":
			case "purchase-credit-note":
				sapDocumentKind = SapDocumentKind.PurchaseCreditNote;
				break;
			case "ausgangsgutschrift":
			case "creditnote":
			case "credit-note":
				sapDocumentKind = SapDocumentKind.CreditNote;
				break;
			default:
				sapDocumentKind = (SapDocumentKind)(-1);
				break;
			}
			kind = sapDocumentKind;
			return Enum.IsDefined(typeof(SapDocumentKind), kind);
		}
	}

	internal static void ConfigureOpenAiInvoiceDocumentClient(HttpClient client)
	{
		client.BaseAddress = new Uri("https://api.openai.com/v1/");
		// Die Dokumentinterpretation besitzt einen eigenen, konfigurierbaren Timeout
		// von bis zu 300 Sekunden. Der HttpClient-Standard von 100 Sekunden darf
		// diesen nicht vorzeitig und mit einer irreführenden Fehlermeldung abbrechen.
		client.Timeout = Timeout.InfiniteTimeSpan;
	}
}
