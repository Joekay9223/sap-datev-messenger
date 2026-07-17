import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../src/NovaNein.Server/wwwroot/", import.meta.url);

test("cockpit exposes sortable worklist columns", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  for (const field of ["docNum", "invoiceNumber", "businessPartner", "documentDate", "entryDate", "grossAmount", "status"]) {
    assert.match(html, new RegExp(`data-sort="${field}"`));
  }
});

test("ignored work items require an admin password and remain visibly struck through", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  const styles = await readFile(new URL("styles.css", root), "utf8");
  const server = await readFile(new URL("../Program.cs", root), "utf8");
  const service = await readFile(new URL("../WorkItemService.cs", root), "utf8");
  assert.match(html, /id="admin-ignore-form" novalidate/);
  assert.match(html, /id="admin-ignore-user"[^>]*autocomplete="username"/);
  assert.match(html, /id="admin-ignore-password"[^>]*type="password"/);
  assert.match(html, /id="admin-ignore-reason"[^>]*maxlength="500"/);
  assert.match(script, /case "ignore": openAdminIgnore\(item, false\)/);
  assert.match(script, /case "restore-ignore": openAdminIgnore\(item, true\)/);
  assert.match(script, /adminPassword: password/);
  assert.match(script, /class="\$\{item\.ignored \? "is-ignored" : ""\}"/);
  assert.match(styles, /tr\.is-ignored td:not\(\.status-cell\):not\(:last-child\)[^{]*\{[^}]*text-decoration: line-through/);
  assert.match(server, /identities\.AuthenticateAsync\(request\.AdminUserName, request\.AdminPassword/);
  assert.match(server, /RequireRateLimiting\("web-login"\)/);
  assert.match(service, /active = all\.Where\(item => !item\.Ignored\)/);
  assert.match(service, /OverallState = "ignored"/);
});

test("cockpit review guards use the defined trusted HTTP helper", async () => {
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(script, /function isTrustedHttpReviewMode\(\)/);
  assert.doesNotMatch(script, /isDummyHttpReviewMode/);
  assert.match(script, /button\.setAttribute\("aria-pressed", String\(active\)\)/);
});

test("cockpit keeps navigation and KPI semantics accessible on narrow screens", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const styles = await readFile(new URL("styles.css", root), "utf8");
  assert.match(html, /<nav class="topbar-nav" aria-label="Hauptnavigation">/);
  assert.match(html, /<link rel="icon" href="\/favicon\.svg\?v=20260716-navbuttons3"/);
  assert.match(html, /data-filter="review" aria-pressed="false" aria-label="Fachliche Abweichungen anzeigen"/);
  assert.match(html, /Fachliche Abweichungen/);
  assert.match(html, /data-filter="error" aria-pressed="false" aria-label="Technische Verarbeitungsfehler anzeigen"/);
  assert.match(html, /Technische Verarbeitungsfehler/);
  assert.match(styles, /\.topbar-nav \{[^}]*min-width: 0;/);
  assert.match(styles, /@media \(max-width: 820px\)/);
  assert.match(styles, /\.topbar \{ flex-wrap: wrap;/);
  assert.match(styles, /\.table-wrap \{ width: 100%; min-width: 0;[^}]*overflow-x: auto;/);
  assert.match(styles, /\.table-wrap \{[^}]*contain: layout paint;/);
  assert.match(styles, /\.worklist-panel \{ overflow-x: hidden; \}/);
});

test("cockpit displays and searches the supplier invoice number", async () => {
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(script, /invoiceNumber:\s*source\.invoiceNumber/);
  assert.match(script, /item\.invoiceNumber, item\.businessPartner/);
  assert.match(script, /class="supplier-invoice-cell"/);
});

test("cockpit keeps the sortable table inside its work area", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  const styles = await readFile(new URL("styles.css", root), "utf8");
  assert.match(html, /<col class="status-column">/);
  assert.match(html, /<col class="action-column">/);
  assert.match(styles, /\.worklist-table \{[^}]*min-width: 920px;[^}]*table-layout: fixed;/);
  assert.match(styles, /\.worklist-table \.action-column \{ width: 23%; \}/);
  assert.match(styles, /\.row-action \{[^}]*display: inline-flex;[^}]*min-height: 26px;[^}]*white-space: nowrap;/);
  assert.match(script, /action\.key === "download"\) return "ZIP laden"/);
  assert.match(script, /action\.key === "details"\) return "Status"/);
  assert.match(script, /title="\$\{html\(action\.label\)\}" aria-label="\$\{html\(action\.label\)\}"/);
  assert.match(styles, /\.worklist-table \.date-cell \{ display: table-cell;/);
  assert.match(styles, /@media \(max-width: 1360px\) \{ \.workspace-grid \{ grid-template-columns: 1fr; \}/);
  assert.doesNotMatch(styles, /\.worklist-table \{[^}]*min-width: 1120px;/);
});

test("clicking a non-interactive part of a row opens preview comments and timeline", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  const styles = await readFile(new URL("styles.css", root), "utf8");
  const server = await readFile(new URL("../Program.cs", root), "utf8");
  const sapSql = await readFile(new URL("../SqlSapReadClient.cs", root), "utf8");
  assert.match(html, /id="document-detail-dialog"/);
  assert.match(html, /id="document-detail-preview-frame"/);
  assert.match(html, /id="document-comments"/);
  assert.match(html, /id="document-timeline"/);
  assert.match(script, /event\.target\.closest\("button, a, input, select, textarea, label, \[role='button'\]"\)/);
  assert.match(script, /openRowDetails\(items\[number\(row\.dataset\.index\)\]\)/);
  assert.match(script, /\/api\/v1\/documents\/by-sap\/\$\{encodeURIComponent\(item\.direction\)\}/);
  assert.match(script, /\/api\/v1\/documents\/\$\{encodeURIComponent\(item\.id\)\}\/events/);
  assert.match(script, /\/ignore-history/);
  assert.match(script, /sap\.comments/);
  assert.match(styles, /\.document-detail-layout \{[^}]*grid-template-columns:/);
  assert.match(styles, /\.document-detail-preview iframe \{[^}]*height: 100%;/);
  assert.match(server, /\/api\/v1\/work-items\/\{sapKind\}\/\{docEntry:int\}\/ignore-history/);
  assert.match(sapSql, /d\.\[Comments\]/);
});

test("cockpit sorts by double-clicking the complete header cell", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(html, /app\.js\?v=20260716-booking-cockpit-link1/);
  assert.match(script, /header\.addEventListener\("dblclick"/);
  assert.match(script, /activateSort\(header\.dataset\.sortColumn\)/);
  assert.match(script, /event\.detail === 0/);
  assert.match(script, /setTimeout\(\(\) => \{/);
});

test("PDF assignment targets are independent of sorting and pagination", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(html, /app\.js\?v=20260716-booking-cockpit-link1/);
  assert.match(script, /workPayload\.uploadTargets\.map\(normalizeWorkItem\)/);
  assert.match(script, /state\.hasCompleteUploadTargets \? state\.uploadTargets : state\.items/);
  assert.match(script, /\[\.\.\.state\.uploadTargets, \.\.\.state\.items\]\.find/);
});

test("worklist search is sent to the server instead of only filtering the first page", async () => {
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(script, /params\.set\("search", state\.search\.trim\(\)\)/);
  assert.match(script, /searchTimer = window\.setTimeout/);
  assert.match(script, /loadDashboard\(\);\s*\}, 300\)/);
});

test("a direct document search clears saved task filters and keeps newest SAP documents first", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(html, /app\.js\?v=20260716-booking-cockpit-link1/);
  assert.match(script, /if \(initialSearch\) \{\s*state\.filter = "all";\s*state\.direction = "all";/);
  assert.match(script, /const fallback = number\(left\.docNum\) - number\(right\.docNum\);\s*return state\.sortDirection === "desc" \? -fallback : fallback;/);
});

test("cockpit keeps selected filters and sorting across reloads", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(script, /VIEW_PREFERENCES_KEY = "novanein\.cockpit\.view\.v1"/);
  assert.match(script, /sortBy: "entryDate",\s*sortDirection: "desc"/);
  assert.match(script, /localStorage\.getItem\(VIEW_PREFERENCES_KEY\)/);
  assert.match(script, /localStorage\.setItem\(VIEW_PREFERENCES_KEY/);
  assert.match(script, /restoreViewPreferences\(\);[\s\S]*applyViewPreferencesToControls\(\);/);
  assert.match(script, /state\.direction = event\.target\.value; saveViewPreferences\(\); loadDashboard\(\);/);
  assert.match(script, /state\.dateDays = event\.target\.value; saveViewPreferences\(\); loadDashboard\(\);/);
  assert.match(html, /data-sort="entryDate">Anlagedatum <span class="sort-indicator" aria-hidden="true">↓<\/span>/);
});

test("cockpit uses a generic rolling 90-day baseline", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(html, /<option value="90" selected>Letzte 90 Tage<\/option>/);
  assert.match(script, /dateDays: "90"/);
  assert.doesNotMatch(script, /WORKLIST_START_DATE|since-start|2026-0[67]-01/);
});

test("cockpit metrics use the complete server summary and pagination continues beyond 100 items", async () => {
  const script = await readFile(new URL("app.js", root), "utf8");
  const renderMetrics = script.match(/function renderMetrics\(\) \{([\s\S]*?)\r?\n  \}\r?\n\r?\n  function isTrustedHttpReviewMode/);
  assert.ok(renderMetrics, "renderMetrics function must exist");
  assert.match(renderMetrics[1], /Object\.prototype\.hasOwnProperty\.call\(stats, key\)/);
  assert.doesNotMatch(renderMetrics[1], /state\.items\.length \? fromItems/);
  assert.match(script, /payload\.nextPage \|\| payload\.nextCursor \|\| payload\.next\?\.page \|\| payload\.next\?\.cursor/);
});

test("DATEV-ready metric and filter use the same unfinished-transfer definition", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  const server = await readFile(new URL("../WorkItemService.cs", root), "utf8");
  const summary = await readFile(new URL("../WorkItemSummary.cs", root), "utf8");
  assert.match(summary, /int ReadyForDatev/);
  assert.match(server, /active\.Count\(IsReadyForDatev\)/);
  assert.match(server, /item\.Stages\.Package\.Complete/);
  assert.match(server, /"credit-note-release"/);
  assert.match(server, /requestedDatevStatus, "ready"/);
  assert.match(script, /ready: number\(value\.readyForDatev \?\? value\.datevReady/);
  assert.match(script, /needsCreditNoteRelease/);
  assert.match(script, /credit-note-release/);
  assert.match(script, /params\.set\("datevStatus", "ready"\)/);
  assert.match(script, /state\.filter === "ready" \? !isReadyForDatev\(item\)/);
  assert.match(script, /saveViewPreferences\(\); loadDashboard\(\);/);
  assert.doesNotMatch(script, /params\.set\("datevStatus", "prepared"\)/);
  assert.match(html, /app\.js\?v=20260716-booking-cockpit-link1/);
});

test("a target selected from the worklist survives subsequent PDF selection", async () => {
  const script = await readFile(new URL("app.js", root), "utf8");
  const setFile = script.match(/function setFile\(file\) \{([\s\S]*?)\r?\n  \}\r?\n\r?\n  function showUploadFeedback/);
  assert.ok(setFile, "setFile function must exist");
  assert.doesNotMatch(setFile[1], /state\.selectedTarget = null/);
  assert.match(setFile[1], /state\.selectedTarget\.docNum \|\| state\.selectedTarget\.docEntry/);
});

test("PDF actions open a modal with browse and drag-and-drop controls", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  const styles = await readFile(new URL("styles.css", root), "utf8");
  assert.match(html, /<dialog class="dialog upload-dialog" id="upload-dialog"/);
  assert.match(html, /<strong>PDF durchsuchen<\/strong>/);
  assert.match(html, /oder PDF hier hineinziehen/);
  assert.match(script, /case "upload": openUploadDialog\(item\)/);
  assert.match(script, /dialog\.showModal\(\)/);
  assert.match(styles, /\.upload-dialog \{[^}]*height: auto;/);
});

test("cockpit uses the complete width for the worklist without a permanent upload sidebar", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const styles = await readFile(new URL("styles.css", root), "utf8");
  assert.doesNotMatch(html, /class="side-column"/);
  assert.doesNotMatch(html, /id="open-upload-dialog"/);
  assert.match(styles, /\.workspace-grid \{[^}]*grid-template-columns: minmax\(0, 1fr\);/);
  assert.match(html, /href="\/history\.html">Historie<\/a>/);
});

test("yellow and red validation results open the reasoned manual review", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(html, /Fachliche Abweichungen/);
  assert.match(script, /validationState \|\| ""\)\.toLowerCase\(\) === "rejected"/);
  assert.match(script, /Eine begründete manuelle Freigabe ist möglich/);
  assert.match(script, /event\.kind === "ValidationCompleted"/);
  assert.match(script, /params\.set\("status", "manual-review"\)/);
  assert.match(script, /case "review": openReview\(item\)/);
  assert.match(script, /\$\("#approve-button"\)\.hidden = false/);
  assert.match(html, /id="approve-button"[^>]*>Freigeben<\/button>/);
});

test("approved credit notes open a reasoned DATEV release instead of read-only details", async () => {
  const script = await readFile(new URL("app.js", root), "utf8");
  const server = await readFile(new URL("../Program.cs", root), "utf8");
  const store = await readFile(new URL("../DocumentStore.cs", root), "utf8");
  assert.match(script, /case "credit-note-release": openCreditNoteRelease\(item\)/);
  assert.match(script, /Gutschrift für DATEV freigeben/);
  assert.match(script, /Gutschrift freigeben/);
  assert.match(script, /\$\("#reject-button"\)\.hidden = true/);
  assert.match(script, /\/credit-note-release`, \{ approve: true, reason \}/);
  assert.match(store, /CreditNoteDatevReleaseApproved/);
  assert.match(server, /EnsureEnqueuedAsync\(id, DocumentJobKind\.CreateDatevPackage/);
});

test("DATEV transfer action opens the explicit confirmation dialog", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(html, /id="transfer-dialog"/);
  assert.match(html, /id="transfer-confirm"/);
  assert.match(html, /id="transfer-submit"[^>]*disabled/);
  assert.match(script, /case "transfer": openTransfer\(item\); break;/);
  assert.match(script, /\/transfer-requests`, \{ packageSha256, confirm: true \}/);
});

test("targeted uploads defer OpenAI document interpretation and cannot time out before assignment", async () => {
  const script = await readFile(new URL("app.js", root), "utf8");
  const server = await readFile(new URL("../Program.cs", root), "utf8");
  const inbox = await readFile(new URL("../PdfInboxService.cs", root), "utf8");
  assert.match(script, /postForm: \(path, formData, options = \{\}\) => request\(path, \{ \.\.\.options, method: "POST", body: formData \}\)/);
  assert.match(script, /PDF_UPLOAD_TIMEOUT_MS = 720_000/);
  assert.match(script, /form\.append\("skipExtraction", "true"\)/);
  assert.match(script, /api\.postForm\("\/api\/v1\/pdf-inbox", form, \{ timeoutMs: PDF_UPLOAD_TIMEOUT_MS \}\)/);
  assert.match(server, /form\["skipExtraction"\]/);
  assert.match(server, /inbox\.UploadAsync\(file\.FileName, file\.Length, stream, Actor\(request\), !skipExtraction, ct\)/);
  assert.match(inbox, /if \(extractFacts\)/);
});

test("failed validation allows selecting and uploading a different PDF", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  assert.match(html, /id="replace-pdf-button"[^>]*>Andere PDF hochladen<\/button>/);
  assert.match(script, /\["needs-review", "rejected", "failed"\]\.includes\(validation\)/);
  assert.match(script, /openUploadDialog\(item, true\)/);
  assert.match(script, /\/replacement-pdf`, form/);
  assert.match(script, /Andere PDF prüfen und verknüpfen/);
});

test("successful PDF upload shows animated validation progress and opens its protocol", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const script = await readFile(new URL("app.js", root), "utf8");
  const styles = await readFile(new URL("styles.css", root), "utf8");
  assert.match(html, /id="processing-dialog"/);
  assert.match(html, /id="processing-status" role="status"/);
  assert.match(html, /Das Überprüfungsprotokoll öffnet sich automatisch/);
  assert.match(script, /assignment && \(assignment\.document \|\| assignment\.Document\)/);
  assert.match(script, /await followValidation\(document, target\)/);
  assert.match(script, /api\.get\(`\/api\/v1\/documents\/\$\{encodeURIComponent\(documentId\)\}`/);
  assert.match(script, /openValidationProtocol\(document, target, result\.validation\)/);
  assert.match(script, /openDetails\(item, "Überprüfungsprotokoll"\)/);
  assert.match(styles, /\.processing-dialog \{[^}]*height: auto;/);
  assert.match(styles, /@keyframes processing-scan/);
  assert.match(styles, /\.processing-dialog\[data-phase="success"\] \.processing-check/);
});

test("PDF previews remain directly scrollable in review and detail dialogs", async () => {
  const html = await readFile(new URL("index.html", root), "utf8");
  const styles = await readFile(new URL("styles.css", root), "utf8");
  assert.match(html, /styles\.css\?v=20260716-users1/);
  assert.match(html, /id="pdf-preview-frame"[^>]*tabindex="0"/);
  assert.match(styles, /\.pdf-preview iframe \{ pointer-events: auto; touch-action: pan-y; \}/);
  assert.doesNotMatch(styles, /\.pdf-preview iframe \{ pointer-events: none; \}/);
});
