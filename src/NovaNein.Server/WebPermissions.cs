using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaNein.Server;

public sealed record WebPermissionDefinition(string Key, string Label, string Description);

public static class WebPermissions
{
	public const string DocumentsView = "documents.view";
	public const string DocumentsReview = "documents.review";
	public const string InvoicesView = "invoices.view";
	public const string InvoicesPost = "invoices.post";
	public const string SuppliersManage = "suppliers.manage";
	public const string AccountingView = "accounting.view";
	public const string AccountingManage = "accounting.manage";
	public const string AuditView = "audit.view";
	public const string UsersManage = "users.manage";
	public const string IntegrationsManage = "integrations.manage";
	public const string PaperlessView = "paperless.view";

	public static readonly IReadOnlyList<WebPermissionDefinition> Catalog =
	[
		new(DocumentsView, "Belege ansehen", "Beleg-Cockpit und PDF-Dokumente öffnen."),
		new(DocumentsReview, "Belege bearbeiten", "PDFs verknüpfen, Prüfungen freigeben und Arbeitsfälle bearbeiten."),
		new(InvoicesView, "Buchungsvorschläge ansehen", "Automatisch erkannte Rechnungen und Kontierungsvorschläge öffnen."),
		new(InvoicesPost, "Rechnungen buchen", "Buchungsvorschläge ändern, ablehnen sowie in SAP freigeben und buchen."),
		new(SuppliersManage, "Lieferanten anlegen", "Neue Kreditorenstämme freigeben und in SAP erzeugen."),
		new(AccountingView, "Buchhaltungsabgleich ansehen", "SAP-/DATEV-Abgleich und vorhandene Importe lesen."),
		new(AccountingManage, "Buchhaltungsabgleich bearbeiten", "DATEV-Importe hochladen, bestätigen und Abweichungen entscheiden."),
		new(AuditView, "Protokolle ansehen", "Beleg- und Benutzeraktivitäten mit Bearbeiter einsehen."),
		new(UsersManage, "Benutzer verwalten", "Konten, Rollen, Berechtigungen und Kennwörter verwalten."),
		new(IntegrationsManage, "Integrationen verwalten", "Gmail-Synchronisierung und technische Zuordnungen administrieren."),
		new(PaperlessView, "Paperless ansehen", "Paperless-Dokumente und Zuordnungsvorschläge öffnen.")
	];

	private static readonly HashSet<string> Known = Catalog.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

	public static IReadOnlyList<string> DefaultsForRole(string role) => role switch
	{
		"Operator" =>
		[
			DocumentsView,
			InvoicesView,
			AccountingView,
			AuditView,
			PaperlessView
		],
		"Reviewer" =>
		[
			DocumentsView,
			DocumentsReview,
			InvoicesView,
			InvoicesPost,
			AccountingView,
			AccountingManage,
			AuditView,
			PaperlessView
		],
		"MasterDataApprover" =>
		[
			DocumentsView,
			InvoicesView,
			SuppliersManage,
			AccountingView,
			AuditView,
			PaperlessView
		],
		"Admin" or "Manager" => Catalog.Select(item => item.Key).ToArray(),
		_ => throw new ArgumentException("Die Rolle muss Operator, Reviewer, MasterDataApprover, Admin oder Manager sein.", nameof(role))
	};

	public static IReadOnlyList<string> Normalize(IEnumerable<string>? permissions, string role)
	{
		IEnumerable<string> source = permissions ?? DefaultsForRole(role);
		string[] normalized = source
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value.Trim())
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		string? unknown = normalized.FirstOrDefault(value => !Known.Contains(value));
		if (unknown != null)
		{
			throw new ArgumentException($"Die Berechtigung '{unknown}' ist unbekannt.", nameof(permissions));
		}
		return normalized;
	}

	public static bool Has(WebUser user, string permission) =>
		user.Role is "Admin" or "Manager" || user.Permissions.Contains(permission, StringComparer.Ordinal);

	public static string RoleLabel(string role) => role switch
	{
		"Manager" => "Manager",
		"Admin" => "Administrator",
		"Reviewer" => "Prüfer",
		"MasterDataApprover" => "Stammdatenfreigabe",
		_ => "Mitarbeiter"
	};
}
