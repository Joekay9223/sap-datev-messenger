using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NovaNein.Domain;

namespace NovaNein.Server;

public sealed class WorkItemService(IConfiguration configuration, ISapServiceLayerClient sap, TransferEvidenceStore transferEvidence, DatevTransferRequestStore transferRequests, DocumentJobQueue documentJobs, WorkItemIgnoreStore ignores)
{
	private sealed record ArchiveRow(Guid DocumentId, DocumentDirection Direction, int DocEntry, int DocNum, SapDocumentKind Kind, string PdfSha256, DocumentStatus Status, ReviewSignal? Signal, DateTimeOffset UpdatedAt);

	private sealed record WorkItemDraft(ArchiveRow? Archive, SapDocumentKind Kind, SapDocumentSnapshot? Snapshot, TransferEvidence? Evidence, DatevTransferRequest? TransferRequest = null, SapAttachmentGap? Gap = null, DocumentJob? PackageJob = null)
	{
		public int DocEntry => Gap?.DocEntry ?? Archive?.DocEntry ?? Snapshot?.DocEntry ?? 0;
	}

	private readonly string _connectionString = "Data Source=" + (configuration["Storage:DatabasePath"] ?? "data/novanein.db");
	private readonly CockpitWorkItemCuration _curation = new(configuration);

	public async Task<WorkItemPage> ListAsync(WorkItemQuery? query = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		if ((object)query == null)
		{
			query = new WorkItemQuery();
		}
		ValidateQuery(query);
		DateOnly today = DateOnly.FromDateTime(DateTime.Today);
		DateOnly parsedStart;
		int defaultLookbackDays = Math.Clamp(configuration.GetValue("Cockpit:DefaultLookbackDays", 90), 1, 3650);
		DateOnly configuredStart = DateOnly.TryParse(configuration["Cockpit:DefaultFromDate"], out parsedStart)
			? parsedStart
			: today.AddDays(-defaultLookbackDays);
		DateOnly from = query.FromEntryDate ?? configuredStart;
		DateOnly to = query.ToEntryDate ?? today;
		if (to < from)
		{
			throw new ArgumentException("Der Endtag darf nicht vor dem Starttag liegen.");
		}
		IReadOnlyList<ArchiveRow> archived = await ReadArchiveRowsAsync(cancellationToken);
		IReadOnlyDictionary<(SapDocumentKind Kind, int DocEntry), WorkItemIgnoreEntry> ignoredByKey = await ignores.ListActiveAsync(cancellationToken);
		Dictionary<(SapDocumentKind Kind, int DocEntry), ArchiveRow> archivedByKey = archived.ToDictionary((ArchiveRow x) => (Kind: x.Kind, DocEntry: x.DocEntry));
		Dictionary<Guid, DatevTransferRequest> requests = (await transferRequests.ListAsync(cancellationToken)).ToDictionary((DatevTransferRequest x) => x.DocumentId);
		IReadOnlyList<SapAttachmentGap> missing = await sap.FindMissingPdfAttachmentsAsync(from, to, cancellationToken);
		Dictionary<(SapDocumentKind Kind, int DocEntry), WorkItemDraft> items = new Dictionary<(SapDocumentKind, int), WorkItemDraft>();
		SapDocumentKind[] values = Enum.GetValues<SapDocumentKind>();
		foreach (SapDocumentKind kind in values)
		{
			foreach (SapDocumentSnapshot snapshot in await sap.ListDocumentsAsync(kind, from, to, cancellationToken))
			{
				DirectionFor(kind);
				archivedByKey.TryGetValue((kind, snapshot.DocEntry), out var archive);
				requests.TryGetValue(archive?.DocumentId ?? Guid.Empty, out var request);
				TransferEvidence transferEvidence = (((object)archive != null) ? (await TryGetEvidenceAsync(archive.DocumentId, cancellationToken)) : null);
				TransferEvidence evidence = transferEvidence;
				DocumentJob packageJob = archive is null ? null : await documentJobs.GetAsync(archive.DocumentId, DocumentJobKind.CreateDatevPackage, cancellationToken);
				items[(kind, snapshot.DocEntry)] = new WorkItemDraft(archive, kind, snapshot, evidence, request, null, packageJob);
				archive = null;
				request = null;
			}
		}
		foreach (ArchiveRow archive in archived)
		{
			_ = archive.Direction;
			SapDocumentKind kind = archive.Kind;
			SapDocumentSnapshot snapshot = await TryGetSnapshotAsync(kind, archive.DocEntry, cancellationToken);
			DateOnly? entryDate = snapshot?.EntryDate ?? snapshot?.DocumentDate;
			if ((!entryDate.HasValue || (!(entryDate < from) && !(entryDate > to))) && ((object)snapshot != null || !(archive.UpdatedAt < from.ToDateTime(TimeOnly.MinValue))))
			{
				TransferEvidence evidence2 = await TryGetEvidenceAsync(archive.DocumentId, cancellationToken);
				requests.TryGetValue(archive.DocumentId, out var request2);
				DocumentJob packageJob2 = await documentJobs.GetAsync(archive.DocumentId, DocumentJobKind.CreateDatevPackage, cancellationToken);
				items[(kind, archive.DocEntry)] = new WorkItemDraft(archive, kind, snapshot, evidence2, request2, null, packageJob2);
			}
		}
		foreach (SapAttachmentGap gap in missing)
		{
			DirectionFor(gap.Kind);
			if (!items.ContainsKey((gap.Kind, gap.DocEntry)))
			{
				SapDocumentSnapshot snapshot2 = await TryGetSnapshotAsync(gap.Kind, gap.DocEntry, cancellationToken);
				DateOnly effectiveDate = snapshot2?.DocumentDate ?? gap.EntryDate;
				if (!(effectiveDate < from) && !(effectiveDate > to))
				{
					items[(gap.Kind, gap.DocEntry)] = new WorkItemDraft(null, gap.Kind, snapshot2, null, null, gap);
				}
			}
		}
		WorkItem[] allItems = items.Values
			.Select(draft => Build(draft, ignoredByKey.GetValueOrDefault((draft.Kind, draft.DocEntry))))
			.Where(_curation.IsVisible)
			.ToArray();
		WorkItem[] uploadTargets = WorkItemUploadTargets.Select(allItems).ToArray();
		WorkItem[] materialized = WorkItemOrdering.Apply(
			allItems.Where(item => Matches(item, query)),
			query.SortBy,
			query.SortDirection).ToArray();
		int skip = (query.Page - 1) * query.PageSize;
		return new WorkItemPage(materialized.Skip(skip).Take(query.PageSize).ToArray(), query.Page, query.PageSize, materialized.Length, (skip + query.PageSize < materialized.Length) ? new int?(query.Page + 1) : ((int?)null), uploadTargets);
	}

	public async Task<WorkItemSummary> SummaryAsync(WorkItemQuery? query = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		if ((object)query == null)
		{
			query = new WorkItemQuery();
		}
		List<WorkItem> all = new List<WorkItem>();
		int page = 1;
		while (true)
		{
			WorkItemPage current = await ListAsync(query with
			{
				Page = page,
				PageSize = 200
			}, cancellationToken);
			all.AddRange(current.Items);
			if (!current.NextPage.HasValue)
			{
				break;
			}
			page = current.NextPage.Value;
		}
		WorkItem[] active = all.Where(item => !item.Ignored).ToArray();
		return new WorkItemSummary(
			active.Length,
			active.Count((WorkItem x) => x.OverallState == "completed"),
			active.Count((WorkItem x) => x.PdfState == "missing"),
			active.Count((WorkItem x) => x.OverallState == "review"),
			active.Count((WorkItem x) => x.OverallState == "blocked"),
			active.Count(IsReadyForDatev),
			active.Count((WorkItem x) => x.Stages.Package.Complete),
			active.Count((WorkItem x) => x.Stages.Watchfolder.Complete),
			active.Count((WorkItem x) => x.Stages.DatevUpload.Complete),
			active.Count((WorkItem x) => x.Stages.DatevFinalization.Complete),
			all.Count(item => item.Ignored));
	}

	private WorkItem Build(WorkItemDraft draft, WorkItemIgnoreEntry? ignore)
	{
		ArchiveRow row = draft.Archive;
		SapDocumentSnapshot snapshot = draft.Snapshot;
		DocumentDirection direction = DirectionFor(draft.Kind);
		SapDocumentKind kind = draft.Kind;
		bool flag = (uint)kind <= 1u;
		bool creditNote = kind.IsCreditNote();
		bool supported = flag || creditNote;
		string validation = (((object)row == null) ? "not-started" : ValidationStateFor(row.Status, row.Signal));
		string pdfState = (((object)row != null) ? "linked" : ((snapshot?.AttachmentEntry).HasValue ? "sap-only" : "missing"));
		string datev = DatevStateFor(draft.Evidence, draft.TransferRequest, draft.PackageJob);
		string error = (((object)row != null && row.Status == DocumentStatus.Failed) ? "Die Verarbeitung ist fehlgeschlagen. Bitte Fehlerdetails öffnen." : ((draft.TransferRequest?.Status == "failed") ? (draft.TransferRequest.LastError ?? "Die DATEV-Übertragung ist fehlgeschlagen.") : null));
		string nextAction = NextAction(pdfState, validation, datev, supported, error, creditNote);
		WorkItemStages stages = BuildStages(row, snapshot, validation, draft.Evidence, draft.TransferRequest);
		string text;
		if (stages.DatevFinalization.Complete)
		{
			text = "completed";
		}
		else
		{
			string text2;
			if (pdfState == "missing")
			{
				text2 = "pending";
			}
			else
			{
				string text3;
				if (!supported)
				{
					text3 = "review";
				}
				else
				{
					bool flag2 = error != null;
					bool flag3 = flag2;
					if (!flag3)
					{
						flag = validation == "failed";
						flag3 = flag;
					}
					text3 = ((flag3 || datev == "failed") ? "blocked" : ((validation is "needs-review" or "rejected") ? "review" : "pending"));
				}
				text2 = text3;
			}
			text = text2;
		}
		string overallState = text;
		string overallLabel = overallState switch
		{
			"completed" => "Buchhalterisch abgeschlossen",
			"blocked" => "Fehler – Aktion erforderlich",
			"review" => "Manuelle Prüfung erforderlich",
			_ => "In Bearbeitung",
		};
		var item = new WorkItem((direction == DocumentDirection.Incoming) ? "incoming" : "outgoing", draft.Kind.ToString(), draft.Gap?.DocEntry ?? row?.DocEntry ?? snapshot?.DocEntry ?? 0, draft.Gap?.DocNum ?? row?.DocNum ?? snapshot?.DocNum ?? 0, snapshot?.InvoiceNumber ?? string.Empty, snapshot?.BusinessPartnerName ?? string.Empty, ((object)snapshot != null) ? new DateOnly?(snapshot.DocumentDate) : draft.Gap?.EntryDate, snapshot?.GrossAmount, snapshot?.Currency ?? string.Empty, row?.DocumentId, pdfState, validation, datev, nextAction, supported, error, row?.UpdatedAt, snapshot?.EntryDate ?? draft.Gap?.EntryDate, DocumentTypeLabel(draft.Kind), overallState, overallLabel, stages, ActionsFor(datev, nextAction).Append(new WorkItemAction("ignore", "Ignorieren")).ToArray());
		if (ignore == null) return item;
		return item with
		{
			NextAction = "Keine Aktion erforderlich",
			OverallState = "ignored",
			OverallLabel = "Ignoriert",
			Actions = [new WorkItemAction("restore-ignore", "Ignorierung aufheben")],
			Ignored = true,
			IgnoredReason = ignore.Reason,
			IgnoredBy = ignore.IgnoredBy,
			IgnoredAt = ignore.IgnoredAt
		};
	}

	private static IReadOnlyList<WorkItemAction> ActionsFor(string datev, string nextAction)
	{
		if (datev == "prepared") return [new("download", "DATEV-ZIP herunterladen"), new("transfer", "Übertragung bestätigen")];
		if (datev == "failed") return [new("download", "DATEV-ZIP herunterladen"), new("retry", "Übertragung wiederholen")];
		if (datev is "bridge-staged" or "queued" or "transferring" or "watchfolder-delivered" or "awaiting-datev-confirmation")
			return [new("download", "DATEV-ZIP herunterladen"), new("details", "Transferstatus anzeigen")];
		if (datev is "finalized" or "upload-succeeded") return [new("download", "DATEV-ZIP herunterladen"), new("details", "Transferstatus anzeigen")];
		return [ActionFromLabel(nextAction)];
	}

	private static WorkItemAction ActionFromLabel(string label)
	{
		var key = label switch
		{
			"PDF hochladen" or "PDF hochladen und archivieren" => "upload",
			"Prüfen und freigeben" or "Prüfgründe anzeigen" => "review",
			"Gutschrift für DATEV freigeben" => "credit-note-release",
			"DATEV-Paket erneut vorbereiten" => "prepare",
			_ => "details"
		};
		return new WorkItemAction(key, label);
	}

	private static bool Matches(WorkItem item, WorkItemQuery query)
	{
		string direction = query.Direction?.Trim();
		string status = query.Status?.Trim();
		string search = query.Search?.Trim();
		if (item.Ignored && (!string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(query.DatevStatus) || query.ErrorStatus.HasValue || query.PdfPresent.HasValue))
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(search) && !SearchValues(item).Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(direction) && !string.Equals(item.Direction, direction, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(status))
		{
			var manualReview = string.Equals(status, "manual-review", StringComparison.OrdinalIgnoreCase);
			var matchesStatus = manualReview
				? item.ValidationState is "needs-review" or "rejected"
				: string.Equals(item.ValidationState, status, StringComparison.OrdinalIgnoreCase)
				  || string.Equals(item.DatevState, status, StringComparison.OrdinalIgnoreCase)
				  || string.Equals(item.PdfState, status, StringComparison.OrdinalIgnoreCase);
			if (!matchesStatus) return false;
		}
		if (!string.IsNullOrWhiteSpace(query.DatevStatus))
		{
			var requestedDatevStatus = query.DatevStatus.Trim();
			var matchesDatevStatus = string.Equals(requestedDatevStatus, "ready", StringComparison.OrdinalIgnoreCase)
				? IsReadyForDatev(item)
				: string.Equals(item.DatevState, requestedDatevStatus, StringComparison.OrdinalIgnoreCase);
			if (!matchesDatevStatus) return false;
		}
		bool? errorStatus = query.ErrorStatus;
		if (errorStatus.HasValue)
		{
			bool hasError = errorStatus == true;
			if (item.Error != null != hasError)
			{
				return false;
			}
		}
		errorStatus = query.PdfPresent;
		if (errorStatus.HasValue)
		{
			bool present = errorStatus == true;
			if (!string.Equals(item.PdfState, "missing", StringComparison.OrdinalIgnoreCase) != present)
			{
				return false;
			}
		}
		return true;
	}

	internal static bool IsReadyForDatev(WorkItem item)
	{
		if (item.Ignored || item.Stages.DatevFinalization.Complete)
		{
			return false;
		}
		return item.Stages.Package.Complete
			|| item.Actions?.Any(action => string.Equals(action.Key, "credit-note-release", StringComparison.OrdinalIgnoreCase)) == true;
	}

	private static IEnumerable<string> SearchValues(WorkItem item)
	{
		yield return item.DocNum.ToString();
		yield return item.DocEntry.ToString();
		yield return item.InvoiceNumber ?? string.Empty;
		yield return item.BusinessPartner ?? string.Empty;
		yield return item.DocumentType ?? string.Empty;
		yield return item.SapKind ?? string.Empty;
		yield return item.Direction ?? string.Empty;
	}

	internal static string ValidationStateFor(DocumentStatus status, ReviewSignal? signal = null)
	{
		if (status == DocumentStatus.Failed && signal.HasValue)
		{
			return "approved";
		}
		switch (status)
		{
		case DocumentStatus.Received:
			return "received";
		case DocumentStatus.Validating:
			return "validating";
		case DocumentStatus.NeedsReview:
			return "needs-review";
		case DocumentStatus.Approved:
		case DocumentStatus.AttachedToSap:
		case DocumentStatus.Packaged:
		case DocumentStatus.Transferred:
			return "approved";
		case DocumentStatus.Rejected:
			return "rejected";
		case DocumentStatus.Failed:
			return "failed";
		default:
			return "not-started";
		}
	}

	private static string DatevStateFor(TransferEvidence? evidence, DatevTransferRequest? request, DocumentJob? packageJob)
	{
		if ((object)evidence == null)
		{
			if (packageJob?.State is DocumentJobState.Queued or DocumentJobState.Running) return "preparing";
			if (packageJob?.State == DocumentJobState.Failed) return "package-failed";
			return "not-prepared";
		}
		if (evidence.IsTransferred)
		{
			return "finalized";
		}
		string text = request?.Status;
		if ((text == "queued" || text == "transferring") ? true : false)
		{
			return request.Status;
		}
		if (text == "bridge-staged") return "bridge-staged";
		text = request?.Status;
		if ((text == "watchfolder-delivered" || text == "delivered") ? true : false)
		{
			return "watchfolder-delivered";
		}
		if (request?.Status == "awaiting-datev-confirmation")
		{
			return "awaiting-datev-confirmation";
		}
		if (request?.Status == "finalized")
		{
			return "finalized";
		}
		if (request?.Status == "failed")
		{
			return "failed";
		}
		if (evidence.UploadSucceededAt.HasValue)
		{
			return "upload-succeeded";
		}
		return "prepared";
	}

	internal static string NextAction(string pdf, string validation, string datev, bool supported, string? error, bool creditNote = false)
	{
		if (pdf == "missing")
		{
			return "PDF hochladen";
		}
		if (pdf == "sap-only")
		{
			return "PDF hochladen und archivieren";
		}
		if (!supported)
		{
			return "Gutschrift fachlich prüfen";
		}
		if (datev == "failed")
		{
			return "Übertragung wiederholen";
		}
		if (datev == "package-failed")
		{
			return "DATEV-Paket erneut vorbereiten";
		}
		bool flag = error != null;
		bool flag2 = flag;
		bool flag3;
		if (!flag2)
		{
			flag3 = validation == "failed";
			flag2 = flag3;
		}
		if (flag2)
		{
			return "Fehlerdetails öffnen";
		}
		switch (validation)
		{
		case "needs-review":
		case "rejected":
			return "Prüfen und freigeben";
		case "received":
		case "validating":
			flag3 = true;
			break;
		default:
			flag3 = false;
			break;
		}
		if (flag3)
		{
			return "Prüfung läuft";
		}
		if (creditNote && validation == "approved" && datev == "not-prepared")
		{
			return "Gutschrift für DATEV freigeben";
		}
		switch (datev)
		{
		case "not-prepared":
			return "DATEV-Paket wird automatisch vorbereitet";
		case "preparing":
			return "DATEV-Paket wird automatisch vorbereitet";
		case "prepared":
			return "Übertragung bestätigen";
		case "bridge-staged":
			return "Lokal bereitgestellt – DATEV-Bridge abwarten";
		case "upload-succeeded":
			return "DATEV-Abschluss abwarten";
		default:
			flag3 = false;
			break;
		}
		if (flag3)
		{
			return "Übertragung bestätigen";
		}
		if ((datev == "queued" || datev == "transferring") ? true : false)
		{
			return "Übertragung läuft";
		}
		if ((datev == "watchfolder-delivered" || datev == "awaiting-datev-confirmation") ? true : false)
		{
			return "An DATEV übergeben – BTTnext-Bestätigung abwarten";
		}
		return "Keine Aktion erforderlich";
	}

	private static WorkItemStages BuildStages(ArchiveRow? row, SapDocumentSnapshot? snapshot, string validation, TransferEvidence? evidence, DatevTransferRequest? request)
	{
		bool archived = (object)row != null;
		bool flag = (snapshot?.AttachmentEntry).HasValue;
		bool flag2 = flag;
		bool flag3;
		if (!flag2)
		{
			DocumentStatus? documentStatus = row?.Status;
			if (documentStatus.HasValue)
			{
				DocumentStatus valueOrDefault = documentStatus.GetValueOrDefault();
				if ((uint)(valueOrDefault - 5) <= 2u)
				{
					flag3 = true;
					goto IL_0070;
				}
			}
			flag3 = false;
			goto IL_0070;
		}
		goto IL_0074;
		IL_0074:
		bool sapAttached = flag2;
		bool validationGreen = validation == "approved";
		bool package = (evidence?.PackagePreparedAt).HasValue;
		switch (request?.Status)
		{
		case "bridge-staged":
		case "watchfolder-delivered":
		case "awaiting-datev-confirmation":
		case "finalized":
			flag3 = true;
			break;
		default:
			flag3 = false;
			break;
		}
		bool watchfolder = request?.Status is "watchfolder-delivered" or "awaiting-datev-confirmation" or "finalized";
		bool bridgeStaged = request?.Status == "bridge-staged";
		bool uploaded = (evidence?.UploadSucceededAt).HasValue;
		bool finalized = (evidence?.UploadSucceededAt).HasValue && evidence.JobFinalizedAt.HasValue;
		return new WorkItemStages(Stage("present", "SAP-Beleg vorhanden", complete: true), Stage(archived ? "archived" : ((snapshot?.AttachmentEntry).HasValue ? "sap-only" : "missing"), archived ? "PDF archiviert" : ((snapshot?.AttachmentEntry).HasValue ? "PDF nur in SAP" : "PDF fehlt"), archived), Stage(sapAttached ? "attached" : "pending", sapAttached ? "PDF an SAP übertragen" : "SAP-Anhang offen", sapAttached), Stage(validationGreen ? "green" : validation, validationGreen ? "Prüfung bestanden" : ((validation == "needs-review") ? "Gelbe Prüfung – manuelle Entscheidung möglich" : ((validation == "rejected") ? "Rote Prüfung – manuelle Freigabe möglich" : "Prüfung offen")), validationGreen), Stage(package ? "prepared" : "pending", package ? "DATEV-ZIP vorbereitet" : "DATEV-ZIP offen", package), Stage(watchfolder ? "delivered" : bridgeStaged ? "staged" : ((request?.Status == "failed") ? "failed" : "pending"), watchfolder ? "DATEV-Watchfolder erreicht" : bridgeStaged ? "Lokal für DATEV bereitgestellt" : ((request?.Status == "failed") ? "Übergabe fehlgeschlagen" : "DATEV-Übergabe offen"), watchfolder), Stage(uploaded ? "uploaded" : "pending", uploaded ? "DATEV-Upload erkannt" : "DATEV-Upload offen", uploaded), Stage(finalized ? "finalized" : "pending", finalized ? "DATEV-Auftrag abgeschlossen" : "DATEV-Abschluss offen", finalized));
		IL_0070:
		flag2 = flag3;
		goto IL_0074;
		static WorkItemStage Stage(string state, string label, bool complete)
		{
			return new WorkItemStage(state, label, complete);
		}
	}

	private static string DocumentTypeLabel(SapDocumentKind kind)
	{
		return kind switch
		{
			SapDocumentKind.PurchaseInvoice => "Eingangsrechnung",
			SapDocumentKind.Invoice => "Ausgangsrechnung",
			SapDocumentKind.PurchaseCreditNote => "Eingangsgutschrift",
			SapDocumentKind.CreditNote => "Ausgangsgutschrift",
			_ => kind.ToString(),
		};
	}

	private async Task<IReadOnlyList<ArchiveRow>> ReadArchiveRowsAsync(CancellationToken cancellationToken)
	{
		List<ArchiveRow> result = new List<ArchiveRow>();
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlyList<ArchiveRow> result2;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT id,direction,doc_entry,doc_num,sap_kind,pdf_sha256,status,signal,updated_at FROM documents ORDER BY updated_at";
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<ArchiveRow> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					result.Add(new ArchiveRow(Guid.Parse(((DbDataReader)(object)reader).GetString(0)), (DocumentDirection)((DbDataReader)(object)reader).GetInt32(1), ((DbDataReader)(object)reader).GetInt32(2), ((DbDataReader)(object)reader).GetInt32(3), ((SapBusinessDocumentType)((DbDataReader)(object)reader).GetInt32(4)).ToServer((DocumentDirection)((DbDataReader)(object)reader).GetInt32(1)), ((DbDataReader)(object)reader).GetString(5), (DocumentStatus)((DbDataReader)(object)reader).GetInt32(6), ((DbDataReader)(object)reader).IsDBNull(7) ? null : (ReviewSignal?)((DbDataReader)(object)reader).GetInt32(7), DateTimeOffset.Parse(((DbDataReader)(object)reader).GetString(8))));
				}
				readOnlyList = result;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result2 = readOnlyList;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result2;
	}

	private async Task<TransferEvidence?> TryGetEvidenceAsync(Guid documentId, CancellationToken cancellationToken)
	{
		try
		{
			return await transferEvidence.GetAsync(documentId, cancellationToken);
		}
		catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
		{
			return null;
		}
	}

	private async Task<SapDocumentSnapshot?> TryGetSnapshotAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken)
	{
		try
		{
			return await sap.GetDocumentAsync(kind, docEntry, cancellationToken);
		}
		catch (Exception ex) when (((Func<bool>)delegate
		{
			// Could not convert BlockContainer to single expression
			bool flag = !(ex is TaskCanceledException) || !cancellationToken.IsCancellationRequested;
			if (flag)
			{
				flag = ((ex is HttpRequestException || ex is InvalidOperationException || ex is TaskCanceledException || ex is SqlException || ex is KeyNotFoundException || ex is NotSupportedException) ? true : false);
			}
			return flag;
		}).Invoke())
		{
			return null;
		}
	}

	private static DocumentDirection DirectionFor(SapDocumentKind kind)
	{
		if (kind == SapDocumentKind.PurchaseInvoice || kind == SapDocumentKind.PurchaseCreditNote)
		{
			return DocumentDirection.Incoming;
		}
		return DocumentDirection.Outgoing;
	}

	private static void ValidateQuery(WorkItemQuery query)
	{
		WorkItemOrdering.Validate(query.SortBy, query.SortDirection);
		if (query.Page < 1)
		{
			throw new ArgumentOutOfRangeException("Page");
		}
		int pageSize = query.PageSize;
		if ((pageSize < 1 || pageSize > 200) ? true : false)
		{
			throw new ArgumentOutOfRangeException("PageSize", "Die Seitengröße muss zwischen 1 und 200 liegen.");
		}
		if (!string.IsNullOrWhiteSpace(query.Direction))
		{
			string direction = query.Direction.Trim().ToLowerInvariant();
			if ((!(direction == "incoming") && !(direction == "outgoing")) || 1 == 0)
			{
				throw new ArgumentException("Richtung muss incoming oder outgoing sein.", "Direction");
			}
		}
	}
}
