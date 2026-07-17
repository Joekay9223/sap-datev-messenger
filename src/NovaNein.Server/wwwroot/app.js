/* global fetch */
(function () {
  "use strict";

  const MAX_PDF_BYTES = 20 * 1024 * 1024;
  const POLL_INTERVAL_MS = 15 * 60_000;
  const PDF_UPLOAD_TIMEOUT_MS = 720_000;
  const VIEW_PREFERENCES_KEY = "novanein.cockpit.view.v1";
  const SORT_FIELDS = new Set(["docNum", "invoiceNumber", "businessPartner", "documentDate", "entryDate", "grossAmount", "status"]);
  const WORK_FILTERS = new Set(["all", "missing", "review", "ready", "error"]);
  const DIRECTIONS = new Set(["all", "incoming", "outgoing"]);
  const DATE_RANGES = new Set(["31", "90", "all"]);
  const FILTER_LABELS = {
    missing: "PDF fehlt",
    review: "Prüfung nötig",
    ready: "Bereit für DATEV",
    error: "Handlung erforderlich"
  };
  const STATUS_TEXT = {
    missing: "PDF fehlt",
    review: "Prüfung nötig",
    ready: "Bereit für DATEV",
    error: "Fehler – Handlung nötig",
    processing: "Prüfung läuft",
    waiting: "BTTnext bestätigt",
    done: "Abgeschlossen",
    ignored: "Ignoriert"
  };
  const TIMELINE_LABELS = {
    SapDocumentCreated: "SAP-Beleg angelegt",
    DocumentReceived: "PDF übernommen",
    ValidationCompleted: "Inhalt geprüft",
    ValidationRetryRequested: "Prüfung erneut angefordert",
    ManualReviewApproved: "Manuell freigegeben",
    ManualReviewRejected: "Manuell abgelehnt",
    PdfReplaced: "PDF ersetzt",
    SapAttachmentVerified: "SAP-Anhang bestätigt",
    DatevPackagePrepared: "DATEV-Paket vorbereitet",
    DatevTransferCompleted: "DATEV-Übertragung abgeschlossen",
    CreditNoteDatevReleaseApproved: "Gutschrift freigegeben",
    Ignored: "Zeile ignoriert",
    Restored: "Ignorierung aufgehoben"
  };
  const COMMENT_EVENT_KINDS = new Set([
    "ValidationRetryRequested",
    "ManualReviewApproved",
    "ManualReviewRejected",
    "CreditNoteDatevReleaseApproved",
    "CreditNoteDatevReleaseRejected"
  ]);

  function datevStatusText(item) {
    const value = String(item.datevState || "").toLowerCase();
    if (item.kind === "missing" || item.kind === "review" || item.kind === "error") return null;
    if (value === "prepared") return "DATEV-ZIP vorbereitet";
    if (value === "queued") return "Transfer wartet";
    if (value === "transferring") return "An DATEV wird übergeben";
    if (value === "bridge-staged") return "Lokal für DATEV bereitgestellt";
    if (value === "watchfolder-delivered" || value === "delivered" || value === "awaiting-datev-confirmation") return "An DATEV übergeben – BTTnext wartet";
    if (value === "upload-succeeded") return "DATEV-Upload erkannt – Abschluss wartet";
    if (value === "finalized") return "An DATEV übertragen";
    if (value === "failed") return "DATEV-Übertragung fehlgeschlagen";
    return null;
  }

  const state = {
    items: [],
    uploadTargets: [],
    hasCompleteUploadTargets: false,
    inbox: [],
    stats: {},
    health: null,
    filter: "all",
    direction: "all",
    dateDays: "90",
    search: "",
    sortBy: "entryDate",
    sortDirection: "desc",
    selectedFile: null,
    selectedTarget: null,
    replacementMode: false,
    validationFollowUp: 0,
    dialogMode: null,
    dialogItem: null,
	adminAction: null,
    rowDetailKey: null,
    role: "mTLS-Sitzung",
    permissions: [],
    accessMode: "",
    loading: false,
    hasWorkItemsEndpoint: true,
    lastUpdated: null,
    pollTimer: null,
    page: 1,
    nextCursor: null,
    healthNoticeShown: false,
    workListError: null
  };

  function restoreViewPreferences() {
    try {
      const saved = JSON.parse(window.localStorage.getItem(VIEW_PREFERENCES_KEY) || "{}");
      if (WORK_FILTERS.has(saved.filter)) state.filter = saved.filter;
      if (DIRECTIONS.has(saved.direction)) state.direction = saved.direction;
      if (DATE_RANGES.has(saved.dateDays)) state.dateDays = saved.dateDays;
      if (SORT_FIELDS.has(saved.sortBy)) state.sortBy = saved.sortBy;
      if (["asc", "desc"].includes(saved.sortDirection)) state.sortDirection = saved.sortDirection;
    } catch {
      // Blocked or malformed local storage must not prevent the cockpit from loading.
    }
  }

  function saveViewPreferences() {
    try {
      window.localStorage.setItem(VIEW_PREFERENCES_KEY, JSON.stringify({
        filter: state.filter,
        direction: state.direction,
        dateDays: state.dateDays,
        sortBy: state.sortBy,
        sortDirection: state.sortDirection
      }));
    } catch {
      // The selected view remains active for the current session if storage is unavailable.
    }
  }

  function applyViewPreferencesToControls() {
    const direction = $("#direction-filter");
    const dateRange = $("#date-filter");
    if (direction) direction.value = state.direction;
    if (dateRange) dateRange.value = state.dateDays;
  }

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));

  class ApiError extends Error {
    constructor(status, message, payload) {
      super(message);
      this.name = "ApiError";
      this.status = status;
      this.payload = payload;
    }
  }

  function csrfToken() {
    const meta = document.querySelector('meta[name="csrf-token"]');
    if (meta && meta.content) return meta.content;
    const cookie = document.cookie.split(";").map(part => part.trim()).find(part => part.startsWith("XSRF-TOKEN="));
    return cookie ? decodeURIComponent(cookie.slice("XSRF-TOKEN=".length)) : "";
  }

  async function ensureCsrfToken() {
    const current = csrfToken();
    if (current) return current;
    try {
      const response = await fetch(apiUrl("/auth/csrf"), { credentials: "include", headers: { Accept: "application/json", "X-Requested-With": "NovaNein-Web" } });
      if (!response.ok) return "";
      const payload = await response.json();
      const token = payload && payload.token ? payload.token : "";
      const meta = document.querySelector('meta[name="csrf-token"]');
      if (meta && token) meta.content = token;
      return token;
    } catch { return ""; }
  }

  function apiUrl(path) {
    const base = window.NovaNeinConfig && window.NovaNeinConfig.apiBase ? window.NovaNeinConfig.apiBase : "";
    if (!base) return path;
    try {
      const configured = new URL(base, window.location.origin);
      // API requests carry the browser session cookie. Do not allow a runtime
      // configuration to silently send it to a different origin.
      if (configured.origin !== window.location.origin) return path;
      return `${configured.origin}${path}`;
    } catch { return path; }
  }

  async function request(path, options = {}) {
    const headers = new Headers(options.headers || {});
    if (!headers.has("Accept")) headers.set("Accept", "application/json");
    if (!headers.has("X-Requested-With")) headers.set("X-Requested-With", "NovaNein-Web");
    if (options.method && !["GET", "HEAD", "OPTIONS"].includes(options.method.toUpperCase())) {
      const token = await ensureCsrfToken();
      if (token) headers.set("X-CSRF-TOKEN", token);
    }
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), options.timeoutMs || 20_000);
    try {
      const response = await fetch(apiUrl(path), {
        ...options,
        headers,
        credentials: "include",
        signal: options.signal || controller.signal
      });
      const contentType = response.headers.get("content-type") || "";
      let payload = null;
      if (response.status !== 204) {
        if (contentType.includes("json")) {
          try { payload = await response.json(); } catch { payload = null; }
        } else {
          try { payload = await response.text(); } catch { payload = null; }
        }
      }
      if (!response.ok) {
        const message = payload && typeof payload === "object"
          ? (payload.error || payload.detail || payload.title || `Serverantwort ${response.status}`)
          : `Serverantwort ${response.status}`;
        throw new ApiError(response.status, message, payload);
      }
      return payload;
    } catch (error) {
      if (error && error.name === "AbortError") throw new ApiError(0, "Die Serverantwort hat zu lange gedauert.");
      if (error instanceof TypeError) throw new ApiError(0, "Der NovaNein-Server ist nicht erreichbar.");
      throw error;
    } finally {
      window.clearTimeout(timeout);
    }
  }

  const api = {
    get: (path, options = {}) => request(path, { ...options, method: "GET" }),
    postJson: (path, value) => request(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) }),
    postForm: (path, formData, options = {}) => request(path, { ...options, method: "POST", body: formData })
  };

  function repairMojibake(value) {
    const text = String(value ?? "");
    if (!/[ÃÂâð]/.test(text)) return text;
    try {
      const bytes = Uint8Array.from(text, character => character.charCodeAt(0) & 0xff);
      return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    } catch {
      return text;
    }
  }

  function html(value) {
    return repairMojibake(value).replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[character]));
  }

  function number(value, fallback = 0) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
  }

  function toDate(value) {
    if (!value) return null;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date;
  }

  function formatDate(value) {
    const date = toDate(value);
    return date ? new Intl.DateTimeFormat("de-DE", { day: "2-digit", month: "2-digit", year: "numeric" }).format(date) : "—";
  }

  function formatDateTime(value) {
    const date = toDate(value);
    return date ? new Intl.DateTimeFormat("de-DE", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" }).format(date) : "—";
  }

  function formatMoney(value, currency = "EUR") {
    const amount = number(value, NaN);
    if (!Number.isFinite(amount)) return "—";
    try { return new Intl.NumberFormat("de-DE", { style: "currency", currency: currency || "EUR" }).format(amount); }
    catch { return `${amount.toFixed(2)} ${currency || "EUR"}`; }
  }

  function localDateString(date) {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, "0");
    const day = String(date.getDate()).padStart(2, "0");
    return `${year}-${month}-${day}`;
  }

  function normalizeDirection(value) {
    const text = String(value ?? "").toLowerCase();
    if (text.includes("out") || text.includes("ausgang") || text === "invoice") return "outgoing";
    return "incoming";
  }

  function isMissing(value) {
    if (value === false || value === 0) return true;
    const text = String(value ?? "").toLowerCase();
    return ["missing", "none", "not-found", "notfound", "false", "fehlt", "nicht vorhanden"].includes(text);
  }

  function isReadyForDatev(item) {
    if (item && item.ignored === true) return false;
    const stages = item && item.stages || {};
    const actions = Array.isArray(item && item.actions) ? item.actions : [];
    const nextAction = item && item.nextAction;
    const needsCreditNoteRelease = actions.some(action => String(action && action.key || "").toLowerCase() === "credit-note-release")
      || String(nextAction && nextAction.key || "").toLowerCase() === "credit-note-release"
      || String(typeof nextAction === "string" ? nextAction : "").toLowerCase().includes("gutschrift für datev freigeben");
    if (needsCreditNoteRelease) return !(stages.datevFinalization && stages.datevFinalization.complete === true);
    if (stages.package && stages.datevFinalization) {
      return stages.package.complete === true && stages.datevFinalization.complete !== true;
    }
    return ["prepared", "queued", "transferring", "bridge-staged", "watchfolder-delivered", "delivered", "awaiting-datev-confirmation", "upload-succeeded"]
      .includes(String(item && item.datevState || "").toLowerCase());
  }

  function statusKind(raw) {
    if (raw.ignored === true) return "ignored";
    const overall = String(raw.overallState || "").toLowerCase();
    if (overall === "completed") return "done";
    if (overall === "blocked") return "error";
    if (overall === "review") return "review";
    const status = String(raw.status ?? raw.workflowState ?? "").toLowerCase();
    const pdfState = raw.pdfState ?? raw.pdfStatus ?? raw.hasPdf;
    const validation = String(raw.validationState ?? raw.validationStatus ?? raw.signal ?? "").toLowerCase();
    const datev = String(raw.datevState ?? raw.transferState ?? raw.packageState ?? "").toLowerCase();
    const statusNumber = Number(raw.status);
    const signalNumber = Number(raw.signal);
    if (isMissing(pdfState) || (!raw.pdfState && raw.hasPdf === false)) return "missing";
    if (raw.supported === false) return "review";
    if (Number.isInteger(signalNumber) && signalNumber === 1) return "review";
    if (Number.isInteger(signalNumber) && signalNumber === 2) return "error";
    if (status.includes("failed") || status.includes("error") || status.includes("rejected") || status.includes("abgelehnt") || datev.includes("failed") || raw.error) return "error";
    if (validation.includes("yellow") || validation.includes("review") || validation.includes("manual") || validation.includes("gelb") || status.includes("review") || status.includes("needsreview")) return "review";
    if (datev.includes("package-failed")) return "error";
    if (isReadyForDatev(raw)) return "ready";
    if (datev.includes("preparing")) return "processing";
    if (datev.includes("not-prepared") || datev.includes("notprepared")) return "ready";
    if (datev.includes("ready") || datev.includes("prepared") || datev.includes("package") || datev.includes("approval") || datev.includes("bereit") || status.includes("packaged")) return "ready";
    if (datev.includes("delivered") || datev.includes("bttnext")) return "waiting";
    if (status.includes("received") || status.includes("validat") || status.includes("queued") || status.includes("processing") || status.includes("inprogress")) return "processing";
    if (datev.includes("final") || datev.includes("upload") || datev.includes("transfer") || status.includes("attached") || status.includes("approved") || status.includes("transferred")) return "done";
    if (Number.isInteger(statusNumber)) {
      if (statusNumber === 2) return "review";
      if (statusNumber === 3 || statusNumber === 8) return "error";
      if (statusNumber === 6) return "ready";
      if (statusNumber === 7 || statusNumber === 5) return "done";
      if (statusNumber === 0 || statusNumber === 1) return "processing";
    }
    if (raw.nextAction || raw.action) return "review";
    return "processing";
  }

  function nextActionFor(item) {
    if (item.kind === "missing") return { key: "upload", label: "PDF hochladen" };
    if (item.kind === "review") return { key: "review", label: "Prüfung öffnen" };
    if (item.kind === "ready") return { key: "transfer", label: "Übertragung prüfen" };
    if (item.kind === "error") return { key: "details", label: "Fehler ansehen" };
    if (item.kind === "processing") return { key: "details", label: "Details ansehen" };
    return { key: "details", label: "Details ansehen" };
  }

  function normalizeAction(value, item) {
    if (value && typeof value === "object" && value.key) return { key: String(value.key), label: repairMojibake(value.label || value.key) };
    if (typeof value === "string" && value.trim()) {
      const text = repairMojibake(value).trim();
      const lower = text.toLowerCase();
      const key = lower.includes("zip herunterladen") ? "download" : lower.includes("paket erneut") ? "prepare" : lower.includes("automatisch vorbereitet") ? "details" : lower.includes("hochladen") ? "upload" : lower.includes("wiederholen") ? "retry" : lower.includes("paket vorbereiten") ? "prepare" : lower.includes("prüfgründe") || lower.includes("details") ? "details" : lower.includes("prüf") || lower.includes("freig") ? "review" : lower.includes("übertragung") || lower.includes("datev") ? "transfer" : lower.includes("fehler") || lower.includes("öffnen") ? "details" : "details";
      return { key, label: text };
    }
    return nextActionFor(item);
  }

  function normalizeWorkItem(raw) {
    const source = raw && typeof raw === "object" ? raw : {};
    const sap = source.sap || {};
    const direction = normalizeDirection(source.direction ?? sap.direction ?? source.kind);
    const docEntry = number(source.docEntry ?? source.documentEntry ?? sap.docEntry ?? sap.DocEntry, 0);
    const docNum = number(source.docNum ?? source.documentNumber ?? sap.docNum ?? sap.DocNum, 0);
    const kind = statusKind(source);
    const item = {
      ...source,
      id: source.id || source.documentId || source.Id || "",
      direction,
      docEntry,
      docNum,
      invoiceNumber: source.invoiceNumber ?? source.supplierInvoiceNumber ?? sap.invoiceNumber ?? sap.supplierInvoiceNumber ?? "",
      businessPartner: source.businessPartner ?? source.businessPartnerName ?? source.cardName ?? sap.businessPartner ?? sap.businessPartnerName ?? "Nicht angegeben",
      documentDate: source.documentDate ?? source.entryDate ?? source.invoiceDate ?? source.createdAt ?? sap.documentDate,
      entryDate: source.entryDate ?? source.creationDate ?? source.createdAt ?? sap.entryDate,
      documentType: source.documentType || source.sapKind || (direction === "outgoing" ? "Ausgangsrechnung" : "Eingangsrechnung"),
      sapKind: source.sapKind || (direction === "outgoing" ? "Invoice" : "PurchaseInvoice"),
      grossAmount: source.grossAmount ?? source.amount ?? source.total ?? source.totalAmount ?? sap.grossAmount,
      currency: source.currency ?? sap.currency ?? "EUR",
      fileName: source.fileName ?? source.originalFileName ?? source.pdfFileName ?? "",
      pdfState: source.pdfState ?? source.pdfStatus ?? (source.hasPdf === false ? "missing" : "present"),
      validationState: source.validationState ?? source.validationStatus ?? source.signal ?? "",
      datevState: source.datevState ?? source.transferState ?? source.packageState ?? "",
      reasons: Array.isArray(source.reasons) ? source.reasons : Array.isArray(source.findings) ? source.findings : [],
      error: source.error || source.errorMessage || source.failureDetail || "",
      kind,
      supported: source.supported !== false
    };
	item.ignored = source.ignored === true;
	item.ignoredReason = repairMojibake(source.ignoredReason || "");
	item.ignoredBy = repairMojibake(source.ignoredBy || "");
	item.ignoredAt = source.ignoredAt || null;
    item.actions = Array.isArray(source.actions) && source.actions.length
      ? source.actions.map(value => normalizeAction(value, item))
      : [normalizeAction(source.nextAction ?? source.action, item)];
    item.nextAction = normalizeAction(source.nextAction ?? source.action, item);
    return item;
  }

  function asArray(payload) {
    if (Array.isArray(payload)) return payload;
    if (!payload || typeof payload !== "object") return [];
    return payload.items || payload.workItems || payload.results || payload.data || [];
  }

  function normalizeStats(payload) {
    const value = payload && typeof payload === "object" ? payload : {};
    const packagesPrepared = number(value.packagesPrepared ?? value.packaged);
    const finalized = number(value.datevFinalized ?? value.completed);
    return {
      total: number(value.total ?? value.count),
      missing: number(value.missingPdf ?? value.missing ?? value.received),
      review: number(value.needsReview ?? value.review ?? value.manualReview),
      ready: number(value.readyForDatev ?? value.datevReady, Math.max(0, packagesPrepared - finalized)),
      error: number(value.blocked ?? value.failed ?? value.errors ?? value.error),
      completed: finalized
    };
  }

  function setConnection(stateName, label) {
    const indicator = $("#connection-indicator");
    const footerDot = $(".footer-dot");
    indicator.dataset.state = stateName;
    $("#connection-label").textContent = label;
    $("#footer-status").textContent = label;
    if (footerDot) footerDot.dataset.state = stateName;
  }

  function updateSession(session) {
    if (!session || typeof session !== "object") return;
    const user = session.user || session.account || session;
    const name = user.displayName || user.name || user.userName || user.username || "Arbeitsplatz";
    const role = Array.isArray(user.roles) ? user.roles.join(" · ") : user.role || user.primaryRole || "mTLS-Sitzung";
    const roleLabel = user.roleLabel || ({ Manager: "Manager", Admin: "Administrator", Reviewer: "Prüfer", MasterDataApprover: "Stammdatenfreigabe", Operator: "Mitarbeiter" }[role] || role);
    state.role = role;
    state.permissions = Array.isArray(user.permissions) ? user.permissions : [];
    state.accessMode = user.accessMode || "";
    const initial = String(name).trim().charAt(0).toUpperCase() || "?";
    $("#user-name").textContent = name;
    $("#user-role").textContent = roleLabel;
    $("#user-avatar").textContent = initial;
    $("#popover-user").textContent = name;
    $("#popover-role").textContent = roleLabel;
    if (user.accessMode === "lan" || user.accessMode === "tailscale-proxy") $("#logout-button").hidden = true;
  }

  function showNotice(message, tone = "warning", persistent = false) {
    const stack = $("#notice-stack");
    const notice = document.createElement("div");
    notice.className = "notice";
    notice.dataset.tone = tone;
    notice.innerHTML = `<span class="notice-icon" aria-hidden="true">${tone === "error" ? "!" : "i"}</span><span>${html(message)}</span><button class="dismiss" type="button" aria-label="Hinweis schließen">×</button>`;
    notice.querySelector(".dismiss").addEventListener("click", () => notice.remove());
    stack.appendChild(notice);
    if (!persistent) window.setTimeout(() => notice.remove(), 12_000);
  }

  function toast(message, tone = "success") {
    const region = $("#toast-region");
    const item = document.createElement("div");
    item.className = "toast";
    item.dataset.tone = tone;
    item.textContent = message;
    region.appendChild(item);
    window.setTimeout(() => item.remove(), 5_500);
  }

  function filteredItems() {
    const search = state.search.trim().toLowerCase();
    return state.items.filter(item => {
      if (state.filter === "ready" ? !isReadyForDatev(item) : state.filter !== "all" && item.kind !== state.filter) return false;
      if (state.direction !== "all" && item.direction !== state.direction) return false;
      if (!search) return true;
      return [item.docNum, item.docEntry, item.invoiceNumber, item.businessPartner, item.fileName, item.kind].some(value => String(value ?? "").toLowerCase().includes(search));
    }).sort(compareWorkItems);
  }

  function compareWorkItems(left, right) {
    const field = state.sortBy;
    const leftValue = field === "status" ? left.overallLabel : left[field];
    const rightValue = field === "status" ? right.overallLabel : right[field];
    const leftMissing = leftValue === null || leftValue === undefined || leftValue === "" || (field === "docNum" && number(leftValue) <= 0);
    const rightMissing = rightValue === null || rightValue === undefined || rightValue === "" || (field === "docNum" && number(rightValue) <= 0);
    if (leftMissing || rightMissing) {
      if (leftMissing && rightMissing) {
        const fallback = number(left.docNum) - number(right.docNum);
        return state.sortDirection === "desc" ? -fallback : fallback;
      }
      return leftMissing ? 1 : -1;
    }

    let result = 0;
    if (["docNum", "grossAmount"].includes(field)) result = number(leftValue) - number(rightValue);
    else if (["documentDate", "entryDate"].includes(field)) result = (toDate(leftValue)?.getTime() || 0) - (toDate(rightValue)?.getTime() || 0);
    else result = String(leftValue).localeCompare(String(rightValue), "de", { sensitivity: "base", numeric: true });
    if (result !== 0) return state.sortDirection === "desc" ? -result : result;
    const fallback = number(left.docNum) - number(right.docNum);
    return state.sortDirection === "desc" ? -fallback : fallback;
  }

  function renderSortHeaders() {
    $$("[data-sort-column]").forEach(header => {
      const field = header.dataset.sortColumn;
      const active = field === state.sortBy;
      header.setAttribute("aria-sort", active ? (state.sortDirection === "desc" ? "descending" : "ascending") : "none");
      const indicator = $(".sort-indicator", header);
      if (indicator) indicator.textContent = active ? (state.sortDirection === "desc" ? "↓" : "↑") : "↕";
      const button = $(".sort-button", header);
      if (button) {
        const label = button.textContent.replace(/[↕↑↓]/g, "").trim();
        button.setAttribute("aria-label", `${label} sortieren${active ? `, aktuell ${state.sortDirection === "desc" ? "absteigend" : "aufsteigend"}` : ""}`);
      }
    });
  }

  function renderMetrics() {
    const fromItems = kind => state.items.filter(item => item.kind === kind).length;
    const stats = state.stats || {};
    const metricValue = (key, kind) => Object.prototype.hasOwnProperty.call(stats, key) ? number(stats[key]) : fromItems(kind);
    $("#metric-missing").textContent = String(metricValue("missing", "missing"));
    $("#metric-review").textContent = String(metricValue("review", "review"));
    $("#metric-ready").textContent = String(metricValue("ready", "ready"));
    $("#metric-error").textContent = String(metricValue("error", "error"));
    $$(".metric").forEach(button => {
      const active = button.dataset.filter === state.filter;
      button.dataset.active = active ? "true" : "false";
      button.setAttribute("aria-pressed", String(active));
    });
  }

  function isTrustedHttpReviewMode() {
    return window.location.protocol === "http:";
  }

  function canReviewDocuments() {
    return ["Admin", "Manager"].includes(state.role)
      || state.permissions.includes("documents.review")
      || (state.accessMode === "workstation-certificate" && state.role === "Reviewer");
  }

  function actionForDisplay(item) {
    const action = item.nextAction || nextActionFor(item);
    const label = String(action.label || "").toLowerCase();
    if (item.kind === "review" && label.includes("prüfgründe")) {
      const canReview = canReviewDocuments();
      return { ...action, key: canReview ? "review" : "details" };
    }
    return action;
  }

  function renderNextAction() {
    const actionable = state.items.find(item => ["missing", "review", "ready", "error"].includes(item.kind));
    const title = $("#next-action-title");
    const detail = $("#next-action-detail");
    const button = $("#next-action-button");
    if (!actionable) {
      if (state.workListError && !state.items.length) {
        title.textContent = "Aufgabenliste nicht verfügbar";
        detail.textContent = state.workListError;
        button.hidden = false;
        button.textContent = "Erneut laden";
        button.onclick = () => loadDashboard();
        return;
      }
      title.textContent = state.loading ? "Arbeitsliste wird geladen …" : "Keine offene Aufgabe";
      detail.textContent = state.loading ? "Einen Augenblick bitte." : "Sobald ein Beleg Ihre Aufmerksamkeit braucht, erscheint er hier.";
      button.hidden = true;
      return;
    }
    const action = actionForDisplay(actionable);
    title.textContent = action.label;
    detail.textContent = `${actionable.direction === "outgoing" ? "Ausgang" : "Eingang"} · Beleg ${actionable.docNum || actionable.docEntry || "ohne Nummer"}`;
    button.hidden = false;
    button.textContent = `${action.label} `;
    const arrow = document.createElement("span"); arrow.setAttribute("aria-hidden", "true"); arrow.textContent = "→"; button.appendChild(arrow);
    button.onclick = () => handleAction(action.key, actionable);
  }

  function stageTone(stage) {
    const value = String(stage && stage.state || "pending").toLowerCase();
    if (stage && stage.complete) return "complete";
    if (value.includes("fail") || value.includes("missing") || value.includes("reject")) return "failed";
    if (value.includes("review") || value.includes("sap-only")) return "review";
    return "pending";
  }

  function renderStages(item, compact = true) {
    if (item.ignored) {
      const detail = item.ignoredReason ? `<small class="ignored-note" title="${html(item.ignoredReason)}">${html(item.ignoredReason)}</small>` : "";
      return `<span class="status-badge ignored">Ignoriert</span>${detail}`;
    }
    const source = item.stages || {};
    const ordered = [source.sap, source.pdfArchive, source.sapAttachment, source.validation, source.package, source.watchfolder, source.datevUpload, source.datevFinalization].filter(Boolean);
    if (!ordered.length) return `<span class="status-badge ${item.kind}">${html(item.overallLabel || datevStatusText(item) || STATUS_TEXT[item.kind] || STATUS_TEXT.processing)}</span>`;
    return `<div class="stage-strip ${compact ? "compact" : "detailed"}" aria-label="Bearbeitungsstatus">${ordered.map(stage => `<span class="stage-light ${stageTone(stage)}" title="${html(stage.label)}"><span aria-hidden="true"></span>${compact ? "" : `<small>${html(stage.label)}</small>`}</span>`).join("")}</div><span class="overall-label ${html(item.kind)}">${html(item.overallLabel || "In Bearbeitung")}</span>`;
  }

  function compactActionLabel(action) {
    if (action.key === "download") return "ZIP laden";
    if (action.key === "details") return "Status";
    if (action.key === "restore-ignore") return "Wieder öffnen";
    return action.label;
  }

  function rowDetailLabel(item) {
    const numberLabel = item.docNum || item.docEntry || "ohne Nummer";
    return `${item.ignored ? "Ignoriert. " : ""}Details zu ${item.documentType || "Beleg"} SAP ${numberLabel} öffnen`;
  }

  function renderWorklist() {
    const body = $("#worklist-body");
    const loading = $("#worklist-loading");
    const empty = $("#worklist-empty");
    const table = $("#worklist-table-wrap");
    const items = filteredItems();
    renderSortHeaders();
    loading.hidden = !state.loading;
    if (state.loading) { table.hidden = true; empty.hidden = true; return; }
    table.hidden = items.length === 0;
    empty.hidden = items.length !== 0;
    if (!items.length) {
      const icon = empty.querySelector(".empty-icon");
      const title = empty.querySelector("strong");
      const detail = empty.querySelector("p");
      const button = empty.querySelector("button");
      if (state.workListError) {
        icon.textContent = "!";
        title.textContent = "Aufgabenliste nicht verfügbar";
        detail.textContent = state.workListError;
        button.textContent = "Erneut laden";
        button.onclick = () => loadDashboard();
      } else {
        icon.textContent = "✓";
        title.textContent = "Alles erledigt";
        detail.textContent = "Für den aktuellen Filter wartet kein Beleg auf Ihre Aktion.";
        button.textContent = "Alle Belege anzeigen";
        button.onclick = null;
      }
    }
    body.innerHTML = items.map((item, index) => {
      const typeLabel = item.documentType || (item.direction === "outgoing" ? "Ausgangsrechnung" : "Eingangsrechnung");
      const actions = item.actions && item.actions.length ? item.actions : [actionForDisplay(item)];
      return `<tr data-index="${index}" class="${item.ignored ? "is-ignored" : ""}" tabindex="0" aria-label="${html(rowDetailLabel(item))}">
        <td><div class="document-cell"><span class="document-id">${html(item.docNum ? `SAP ${item.docNum}` : `SAP ${item.docEntry || "—"}`)}</span><span class="document-type"><span class="direction-mark ${item.direction === "outgoing" ? "outgoing" : ""}">${item.direction === "outgoing" ? "↑" : "↓"}</span>${html(typeLabel)}</span></div></td>
        <td class="supplier-invoice-cell">${item.direction === "incoming" ? html(item.invoiceNumber || "—") : '<span class="not-applicable">—</span>'}</td>
        <td>${html(item.businessPartner)}</td><td class="date-cell">${formatDate(item.documentDate)}</td><td class="date-cell">${formatDate(item.entryDate)}</td><td class="amount-cell">${formatMoney(item.grossAmount, item.currency)}</td>
        <td class="status-cell">${renderStages(item)}</td>
        <td><div class="row-actions">${actions.map(action => `<button class="row-action ${action.key === "details" || action.key === "download" ? "secondary" : ""}" type="button" data-action="${html(action.key)}" data-index="${index}" title="${html(action.label)}" aria-label="${html(action.label)}">${html(compactActionLabel(action))}</button>`).join("")}</div></td>
      </tr>`;
    }).join("");
    $$(".row-action", body).forEach(button => button.addEventListener("click", () => handleAction(button.dataset.action, items[number(button.dataset.index)])));
    $$("tr[data-index]", body).forEach(row => {
      const open = () => openRowDetails(items[number(row.dataset.index)]);
      row.addEventListener("click", event => {
        if (event.target.closest("button, a, input, select, textarea, label, [role='button']")) return;
        open();
      });
      row.addEventListener("keydown", event => {
        if (event.target !== row || (event.key !== "Enter" && event.key !== " ")) return;
        event.preventDefault();
        open();
      });
    });
    $("#row-count").textContent = `${items.length} ${items.length === 1 ? "Beleg" : "Belege"}`;
    $("#load-more").hidden = !state.nextCursor;
  }

  function renderFilter() {
    const active = $("#active-filter");
    if (state.filter === "all") { active.hidden = true; return; }
    active.hidden = false;
    $("#active-filter-label").textContent = `Filter: ${FILTER_LABELS[state.filter] || state.filter}`;
  }

  function populateUploadTargets() {
    const select = $("#upload-target-select");
    const previous = state.selectedTarget ? `${state.selectedTarget.sapKind}:${state.selectedTarget.docEntry}` : "";
    const source = state.hasCompleteUploadTargets ? state.uploadTargets : state.items;
    const candidates = source.filter(item => item.docEntry > 0 && (item.kind === "missing" || item.pdfState === "missing" || !item.id));
    select.innerHTML = `<option value="">Beleg auswählen …</option>${candidates.map(item => `<option value="${html(item.sapKind)}:${item.docEntry}">${html(`${item.documentType} · SAP ${item.docNum || item.docEntry} · ${item.businessPartner}`)}</option>`).join("")}`;
    if (previous && candidates.some(item => `${item.sapKind}:${item.docEntry}` === previous)) select.value = previous;
    if (state.selectedFile) $("#upload-target").hidden = candidates.length === 0;
  }

  function populateAssignTargets() {
    const select = $("#review-target-select");
    if (!select) return;
    const candidates = state.items.filter(item => item.docEntry > 0 && item.kind === "missing");
    select.innerHTML = `<option value="">Beleg auswählen …</option>${candidates.map(item => `<option value="${html(item.sapKind)}:${item.docEntry}">${html(`${item.documentType} · SAP ${item.docNum || item.docEntry} · ${item.businessPartner}`)}</option>`).join("")}`;
    if (state.selectedTarget) select.value = `${state.selectedTarget.sapKind}:${state.selectedTarget.docEntry}`;
  }

  function renderUpload() {
    const selected = state.selectedFile;
    $("#selected-file").hidden = !selected;
    $("#selected-file-name").textContent = selected ? `${selected.name} · ${(selected.size / 1024 / 1024).toFixed(2)} MB` : "";
    $("#upload-target").hidden = !selected || state.replacementMode;
    $("#upload-button").disabled = !(selected && state.selectedTarget && state.selectedTarget.docEntry > 0);
    $("#upload-inbox-button").disabled = !selected;
    $("#upload-inbox-button").hidden = state.replacementMode;
    $("#upload-button").textContent = state.replacementMode ? "Andere PDF prüfen und verknüpfen" : "PDF prüfen und verknüpfen";
    populateUploadTargets();
  }

  function openUploadDialog(target = null, replacementMode = false) {
    state.replacementMode = Boolean(replacementMode);
    if (target) state.selectedTarget = target;
    if (state.replacementMode) {
      state.selectedFile = null;
      $("#pdf-input").value = "";
      $("#upload-title").textContent = "Andere PDF hochladen";
      $("#upload-dialog-intro").textContent = `Wählen Sie die neue PDF für SAP ${target.docNum || target.docEntry}. Der Austausch wird im Prüfprotokoll nachvollziehbar dokumentiert.`;
      showUploadFeedback("Die neue PDF wird sicher gespeichert und anschließend vollständig neu geprüft.", "success");
    } else {
      $("#upload-title").textContent = "PDF hochladen";
      $("#upload-dialog-intro").textContent = "Ziehen Sie die PDF in das Feld oder klicken Sie auf „PDF durchsuchen“.";
      showUploadFeedback("");
    }
    renderUpload();
    const dialog = $("#upload-dialog");
    if (!dialog.open) dialog.showModal();
    window.setTimeout(() => $("#dropzone").focus(), 0);
  }

  function renderInbox() {
    const list = $("#inbox-list");
    const count = $("#inbox-count");
    if (!list || !count) return;
    count.textContent = String(state.inbox.length);
    if (!state.inbox.length) { list.innerHTML = '<div class="mini-empty">Keine unzugeordneten PDFs.</div>'; return; }
    list.innerHTML = state.inbox.map((item, index) => `<div class="inbox-item"><span class="inbox-file-icon">PDF</span><span><span class="inbox-item-name" title="${html(item.fileName)}">${html(item.fileName)}</span><span class="inbox-item-meta">${html(formatDateTime(item.createdAt))}</span></span><button type="button" data-inbox-index="${index}">Zuordnen</button></div>`).join("");
    $$('[data-inbox-index]', list).forEach(button => button.addEventListener("click", () => openInboxAssignment(state.inbox[number(button.dataset.inboxIndex)])));
  }

  function render() {
    renderMetrics();
    renderNextAction();
    renderFilter();
    renderWorklist();
    renderUpload();
    renderInbox();
  }

  function setFile(file) {
    if (!file) return;
    const isPdf = file.type === "application/pdf" || file.name.toLowerCase().endsWith(".pdf");
    if (!isPdf) { showUploadFeedback("Bitte wählen Sie eine PDF-Datei aus.", "error"); return; }
    if (file.size <= 0) { showUploadFeedback("Die ausgewählte Datei ist leer.", "error"); return; }
    if (file.size > MAX_PDF_BYTES) { showUploadFeedback("Die PDF ist größer als 20 MB und wurde nicht übernommen.", "error"); return; }
    state.selectedFile = file;
    showUploadFeedback(state.selectedTarget
      ? `PDF ist bereit und wird mit SAP ${state.selectedTarget.docNum || state.selectedTarget.docEntry} verknüpft.`
      : "PDF ist bereit. Bitte noch den passenden SAP-Beleg auswählen.", "success");
    renderUpload();
  }

  function showUploadFeedback(message, tone = "success") {
    const box = $("#upload-feedback");
    box.hidden = !message;
    box.dataset.tone = tone;
    box.textContent = message || "";
  }

  function parseTarget(value) {
    const [sapKind, docEntry] = String(value || "").split(":");
    const parsed = number(docEntry, 0);
    if (!sapKind || parsed <= 0) return null;
    return [...state.uploadTargets, ...state.items].find(item => item.sapKind === sapKind && item.docEntry === parsed) || { sapKind, docEntry: parsed };
  }

  async function uploadSelectedFile() {
    const file = state.selectedFile;
    const target = state.selectedTarget;
    if (!file || !target) { showUploadFeedback("Bitte PDF und SAP-Beleg auswählen.", "error"); return; }
    const button = $("#upload-button");
    button.disabled = true; button.textContent = "PDF wird geprüft …"; showUploadFeedback("Datei wird sicher übertragen und aus SAP gegengeprüft …", "success");
    const form = new FormData(); form.append("pdf", file, file.name);
    let document = null;
    try {
      if (state.replacementMode) {
        document = await api.postForm(`/api/v1/documents/${encodeURIComponent(target.id)}/replacement-pdf`, form, { timeoutMs: PDF_UPLOAD_TIMEOUT_MS });
        toast("Die andere PDF wurde übernommen und die Prüfung neu gestartet.");
      } else {
        // Bei einem bereits gewählten SAP-Ziel wird die vollständige OpenAI-
        // Dokumentinterpretation im anschließenden Validierungsjob ausgeführt. Der Upload selbst bleibt
        // dadurch schnell und kann nicht mehr vor der Zuordnung auslaufen.
        form.append("skipExtraction", "true");
        const inboxItem = await api.postForm("/api/v1/pdf-inbox", form, { timeoutMs: PDF_UPLOAD_TIMEOUT_MS });
        const assignment = await api.postJson(`/api/v1/pdf-inbox/${encodeURIComponent(inboxItem.id)}/assign`, { direction: target.direction, sapKind: target.sapKind, docEntry: target.docEntry, docNum: target.docNum });
        document = assignment && (assignment.document || assignment.Document);
        toast("PDF wurde übernommen und die Prüfung gestartet.");
      }
    } catch (error) {
      showUploadFeedback(userFacingError(error, "Die PDF konnte nicht übernommen werden."), "error");
      button.disabled = false; button.textContent = state.replacementMode ? "Andere PDF prüfen und verknüpfen" : "PDF prüfen und verknüpfen";
      return;
    }
    state.selectedFile = null; state.selectedTarget = null; state.replacementMode = false; $("#pdf-input").value = ""; showUploadFeedback(""); renderUpload(); $("#upload-dialog").close();
    if (document && (document.id || document.Id)) await followValidation(document, target);
    else { toast("Die Prüfung wurde gestartet. Das Protokoll konnte nicht automatisch geöffnet werden.", "warning"); await loadDashboard(); }
  }

  function documentValidationState(document) {
    const statusValue = document && (document.status ?? document.Status);
    const signalValue = document && (document.signal ?? document.Signal);
    const status = String(statusValue ?? "").toLowerCase();
    const signalText = String(signalValue ?? "").toLowerCase();
    const signal = signalValue == null ? null : Number(signalValue);
    if (signal === 0 || signalText.includes("green")) return { terminal: true, validation: "approved", tone: "success" };
    if (signal === 1 || signalText.includes("yellow")) return { terminal: true, validation: "needs-review", tone: "review" };
    if (signal === 2 || signalText.includes("red")) return { terminal: true, validation: "rejected", tone: "error" };
    if (status === "2" || status.includes("needsreview")) return { terminal: true, validation: "needs-review", tone: "review" };
    if (status === "3" || status.includes("rejected")) return { terminal: true, validation: "rejected", tone: "error" };
    if (["4", "5", "6", "7"].includes(status) || /approved|attached|packaged|transferred/.test(status)) return { terminal: true, validation: "approved", tone: "success" };
    if (status === "8" || status.includes("failed")) return { terminal: true, validation: "failed", tone: "error" };
    return { terminal: false, validation: status === "1" || status.includes("validating") ? "validating" : "received", tone: "processing" };
  }

  function setProcessingState(phase, message) {
    const dialog = $("#processing-dialog");
    dialog.dataset.phase = phase;
    $("#processing-status").textContent = message;
    $(".processing-check").textContent = phase === "review" ? "!" : phase === "error" ? "×" : "✓";
    const step = $("#processing-step-validation");
    step.classList.toggle("active", phase === "processing");
    step.classList.toggle("complete", phase === "success");
    step.classList.toggle("review", phase === "review");
    step.classList.toggle("failed", phase === "error");
  }

  function showProcessingDialog(target) {
    const dialog = $("#processing-dialog");
    dialog.dataset.phase = "processing";
    $("#processing-title").textContent = "Beleg wird geprüft";
    $("#processing-document").textContent = `${target.documentType || (target.direction === "outgoing" ? "Ausgangsrechnung" : "Eingangsrechnung")} · SAP ${target.docNum || target.docEntry}`;
    setProcessingState("processing", "PDF wird gelesen und mit den SAP-Daten abgeglichen …");
    openDialog(dialog);
  }

  function wait(milliseconds) {
    return new Promise(resolve => window.setTimeout(resolve, milliseconds));
  }

  async function followValidation(initialDocument, target) {
    const documentId = initialDocument.id || initialDocument.Id;
    const followUp = ++state.validationFollowUp;
    showProcessingDialog(target);
    let document = initialDocument;
    let checks = 0;
    let connectionErrors = 0;
    const activityMessages = [
      "PDF wird gelesen und mit den SAP-Daten abgeglichen …",
      "Rechnungsnummer, Datum und Betrag werden geprüft …",
      "Geschäftspartner und Beleginhalt werden bewertet …",
      "Prüfergebnis und Protokoll werden vorbereitet …"
    ];
    while (followUp === state.validationFollowUp) {
      const result = documentValidationState(document);
      if (result.terminal) {
        const success = result.validation === "approved";
        const needsReview = result.validation === "needs-review";
        $("#processing-title").textContent = success ? "Prüfung abgeschlossen" : result.validation === "needs-review" ? "Manuelle Prüfung erforderlich" : "Prüfung abgeschlossen";
        setProcessingState(success ? "success" : needsReview ? "review" : "error", success ? "Alles geprüft. Das Überprüfungsprotokoll wird geöffnet …" : "Das Prüfergebnis liegt vor. Das Überprüfungsprotokoll wird geöffnet …");
        await wait(650);
        await loadDashboard();
        if (followUp !== state.validationFollowUp) return;
        $("#processing-dialog").close();
        openValidationProtocol(document, target, result.validation);
        return;
      }
      await wait(checks < 30 ? 1800 : 4000);
      if (followUp !== state.validationFollowUp) return;
      checks += 1;
      setProcessingState("processing", checks > 45
        ? "Die Prüfung dauert bei Scan-PDFs etwas länger. Sie läuft weiterhin …"
        : activityMessages[checks % activityMessages.length]);
      try {
        document = await api.get(`/api/v1/documents/${encodeURIComponent(documentId)}`, { timeoutMs: 12_000 });
        connectionErrors = 0;
      } catch (error) {
        connectionErrors += 1;
        if (connectionErrors >= 3) setProcessingState("processing", "Die Serververbindung wird wiederhergestellt. Die Prüfung läuft weiter …");
      }
    }
  }

  function openValidationProtocol(document, target, validationState) {
    const documentId = document.id || document.Id;
    const liveItem = state.items.find(item => String(item.id) === String(documentId))
      || state.items.find(item => item.sapKind === target.sapKind && item.docEntry === target.docEntry);
    const item = liveItem || normalizeWorkItem({
      ...target,
      id: documentId,
      documentId,
      pdfState: "linked",
      validationState,
      status: document.status ?? document.Status,
      signal: document.signal ?? document.Signal,
      datevState: "not-prepared",
      reasons: []
    });
    const validation = String(item.validationState || validationState || "").toLowerCase();
    if (validation === "needs-review" || validation === "rejected") openReview(item, true);
    else openDetails(item, "Überprüfungsprotokoll");
  }

  async function uploadToInbox() {
    const file = state.selectedFile;
    if (!file) return;
    const button = $("#upload-inbox-button");
    button.disabled = true; button.textContent = "Wird abgelegt …";
    const form = new FormData(); form.append("pdf", file, file.name);
    try {
      await api.postForm("/api/v1/pdf-inbox", form, { timeoutMs: PDF_UPLOAD_TIMEOUT_MS });
      toast("PDF liegt jetzt im Eingang und kann zugeordnet werden.");
      state.selectedFile = null; state.selectedTarget = null; $("#pdf-input").value = ""; showUploadFeedback("PDF wurde ohne Zuordnung gespeichert.", "success"); renderUpload(); $("#upload-dialog").close(); await loadDashboard();
    } catch (error) {
      showUploadFeedback(userFacingError(error, "Die PDF konnte nicht im Eingang abgelegt werden."), "error");
      button.disabled = false; button.textContent = "PDF zunächst ohne Zuordnung ablegen";
    }
  }

  function userFacingError(error, fallback) {
    if (!(error instanceof ApiError)) return fallback;
    if (error.status === 401 || error.status === 403) return "Ihre Sitzung ist nicht berechtigt. Bitte Sitzung prüfen oder den Arbeitsplatz zertifizieren.";
    if (error.status === 409) return error.message || "Dieser Beleg oder diese PDF wurde bereits verarbeitet.";
    if (error.status === 413) return "Die PDF ist zu groß für den NovaNein-Server.";
    if (error.status === 503) return "SAP ist momentan nicht erreichbar. Die PDF wurde nicht gespeichert.";
    return error.message || fallback;
  }

  function itemSummary(item) {
    const pdfUrl = item.id && item.pdfState === "linked" ? `/api/v1/documents/by-sap/${encodeURIComponent(item.direction)}/${encodeURIComponent(item.docEntry)}/pdf?sapKind=${encodeURIComponent(item.sapKind)}` : "";
    return `<strong>${html(item.documentType || (item.direction === "outgoing" ? "Ausgangsrechnung" : "Eingangsrechnung"))} · SAP ${html(item.docNum || item.docEntry || "—")}</strong><span>${html(item.businessPartner)} · ${formatDate(item.documentDate)} · ${formatMoney(item.grossAmount, item.currency)}</span>${pdfUrl ? `<a class="pdf-open-link" href="${pdfUrl}" target="_blank" rel="noopener">PDF öffnen</a>` : ""}`;
  }

  function mayReplacePdf(item) {
    const validation = String(item && item.validationState || "").toLowerCase();
    return Boolean(item && item.id && item.pdfState === "linked" && ["needs-review", "rejected", "failed"].includes(validation));
  }

  async function openReview(item, protocolMode = false) {
    if (!canReviewDocuments()) { toast("Für diese Prüfung fehlt die Berechtigung „Belege bearbeiten“.", "warning"); return; }
    if (!item || !item.id) { toast("Für diesen Beleg gibt es noch keine gespeicherte PDF-Verknüpfung.", "warning"); return; }
    state.dialogMode = "review"; state.dialogItem = item;
    showDocumentPdfPreview(item);
    const isRedReview = String(item.validationState || "").toLowerCase() === "rejected";
    $("#review-dialog-title").textContent = protocolMode ? (isRedReview ? "Überprüfungsprotokoll · rote Prüfung" : "Überprüfungsprotokoll · manuelle Prüfung") : (isRedReview ? "Rote Prüfdetails" : "Prüfdetails");
    $("#review-summary").innerHTML = itemSummary(item);
    const reasons = item.reasons.length ? item.reasons : [isRedReview ? "Die automatische Prüfung hat eine harte Abweichung erkannt. Eine begründete manuelle Freigabe ist möglich." : "Die automatische Prüfung bittet um eine manuelle Entscheidung."];
    $("#review-reasons").innerHTML = reasons.map(reason => `<div class="reason-item ${isRedReview ? "error" : ""}">${html(reason)}</div>`).join("");
    $("#review-reason").value = ""; $("#review-reason").closest(".reason-field").hidden = false;
	$("#review-reason").placeholder = "Kurz erklären, warum Sie freigeben oder ablehnen …";
    $("#assign-target-wrap").hidden = true;
    $("#approve-button").hidden = false; $("#reject-button").hidden = false; $("#approve-button").textContent = "Freigeben"; $("#reject-button").textContent = "Ablehnen";
    $("#replace-pdf-button").hidden = !mayReplacePdf(item);
	$$('button', $("#review-form")).forEach(button => button.disabled = false);
    $("#review-feedback").hidden = true;
    openDialog($("#review-dialog"));
    try {
      const events = asArray(await api.get(`/api/v1/documents/${encodeURIComponent(item.id)}/events`));
      const validationEvents = events.filter(event => event.kind === "ValidationCompleted" && event.detail);
      if (!item.reasons.length && validationEvents.length) {
        $("#review-reasons").innerHTML = validationEvents.map(event => `<div class="reason-item ${isRedReview ? "error" : ""}">${html(event.detail)}</div>`).join("");
      }
    } catch { /* Die manuelle Entscheidung bleibt auch ohne Audit-Nachladen verfügbar. */ }
  }

  function openCreditNoteRelease(item) {
    if (!canReviewDocuments()) { toast("Für diese Freigabe fehlt die Berechtigung „Belege bearbeiten“.", "warning"); return; }
    if (!item || !item.id) { toast("Für diese Gutschrift gibt es noch keine gespeicherte PDF-Verknüpfung.", "warning"); return; }
    state.dialogMode = "credit-note-release"; state.dialogItem = item;
    showDocumentPdfPreview(item);
    $("#review-dialog-title").textContent = "Gutschrift für DATEV freigeben";
    $("#review-summary").innerHTML = itemSummary(item);
    $("#review-reasons").innerHTML = '<div class="reason-item">Bitte prüfen Sie die Gutschrift und geben Sie sie ausdrücklich für Paketbildung und DATEV-Übertragung frei.</div>';
    $("#review-reason").value = "";
    $("#review-reason").closest(".reason-field").hidden = false;
    $("#review-reason").placeholder = "Kurz begründen, warum die Gutschrift an DATEV übergeben werden darf …";
    $("#assign-target-wrap").hidden = true;
    $("#approve-button").hidden = false;
    $("#approve-button").textContent = "Gutschrift freigeben";
    $("#reject-button").hidden = true;
    $("#replace-pdf-button").hidden = !mayReplacePdf(item);
	$$('button', $("#review-form")).forEach(button => button.disabled = false);
    $("#review-feedback").hidden = true;
    openDialog($("#review-dialog"));
  }

  async function openDetails(item, title = "Belegdetails") {
    if (!item) return;
    state.dialogMode = "details"; state.dialogItem = item;
    if (item.id && item.pdfState === "linked") showDocumentPdfPreview(item); else hidePdfPreview();
    $("#review-dialog-title").textContent = title;
    $("#review-summary").innerHTML = `${itemSummary(item)}${renderStages(item, false)}`;
    const reasons = [item.error || "Kein offener Fehler gemeldet.", `Gesamtstatus: ${item.overallLabel || STATUS_TEXT[item.kind] || item.kind}`];
    $("#review-reasons").innerHTML = reasons.map(reason => `<div class="reason-item ${item.kind === "error" ? "error" : ""}">${html(reason)}</div>`).join("");
    $("#review-reason").closest(".reason-field").hidden = true; $("#approve-button").hidden = true; $("#reject-button").hidden = true;
    $("#replace-pdf-button").hidden = !mayReplacePdf(item);
    $("#assign-target-wrap").hidden = true;
    $("#review-feedback").hidden = true; openDialog($("#review-dialog"));
    if (item.id) {
      try {
        const events = asArray(await api.get(`/api/v1/documents/${encodeURIComponent(item.id)}/events`));
        if (events.length) $("#review-reasons").insertAdjacentHTML("beforeend", events.map(event => `<div class="audit-event"><time>${html(formatDateTime(event.occurredAt))}</time><span>${html(event.detail || event.kind)}</span><small>${html(event.actor || "System")}</small></div>`).join(""));
      } catch { /* Die Statusansicht bleibt auch ohne Ereignisdetails nutzbar. */ }
      try {
        const datev = await api.get(`/api/v1/documents/${encodeURIComponent(item.id)}/datev`);
        const transferDetails = [
          datev.transferState && `Transferstatus: ${datev.transferState}`,
          datev.bridgeStagedAt && `Lokal bereitgestellt: ${formatDateTime(datev.bridgeStagedAt)}`,
          datev.watchfolderDeliveredAt && `DATEV-Dateiserver erreicht: ${formatDateTime(datev.watchfolderDeliveredAt)}`,
          datev.uploadSucceededAt && `DATEV-Upload erkannt: ${formatDateTime(datev.uploadSucceededAt)}`,
          datev.jobFinalizedAt && `DATEV-Auftrag abgeschlossen: ${formatDateTime(datev.jobFinalizedAt)}`,
          datev.bttnextWaiting && "BTTnext hat seit mehr als 15 Minuten keinen Abschluss gemeldet. Bitte prüfen Sie, ob BTTnext in der vorgesehenen DATEV-Windows-Sitzung läuft.",
          datev.transferError
        ].filter(Boolean);
        if (transferDetails.length) $("#review-reasons").insertAdjacentHTML("beforeend", transferDetails.map(detail => `<div class="reason-item ${datev.transferError ? "error" : ""}">${html(detail)}</div>`).join(""));
      } catch { /* Der Dokumentstatus bleibt sichtbar. */ }
    }
  }

  function detailFact(label, value, title = value) {
    return `<div class="document-detail-fact"><span>${html(label)}</span><strong title="${html(title || "—")}">${html(value || "—")}</strong></div>`;
  }

  function detailEventLabel(kind) {
    if (TIMELINE_LABELS[kind]) return TIMELINE_LABELS[kind];
    return String(kind || "Status geändert").replace(/([a-zäöü])([A-ZÄÖÜ])/g, "$1 $2");
  }

  function detailEventTone(kind) {
    const value = String(kind || "").toLowerCase();
    if (value.includes("failed") || value.includes("rejected") || value.includes("error")) return "error";
    if (value.includes("ignored") || value.includes("retry") || value.includes("review")) return "warning";
    return "";
  }

  function detailTime(value) {
    const text = String(value || "");
    return /^\d{4}-\d{2}-\d{2}$/.test(text) ? formatDate(text) : formatDateTime(text);
  }

  function renderDetailComments(comments) {
    const target = $("#document-comments");
    $("#document-comments-count").textContent = String(comments.length);
    target.innerHTML = comments.length
      ? comments.map(comment => `<article class="document-comment"><p>${html(comment.text)}</p><footer><strong>${html(comment.source)}</strong><span>${html(comment.actor || "")}${comment.occurredAt ? ` · ${html(detailTime(comment.occurredAt))}` : ""}</span></footer></article>`).join("")
      : '<div class="detail-empty">Zu diesem Beleg sind keine Kommentare hinterlegt.</div>';
  }

  function renderDetailTimeline(item, events, ignoreHistory) {
    const timeline = [];
    const sapDate = item.entryDate || item.documentDate;
    if (sapDate) timeline.push({ occurredAt: sapDate, kind: "SapDocumentCreated", detail: `${item.documentType || "Beleg"} SAP ${item.docNum || item.docEntry} wurde angelegt.`, actor: "SAP" });
    events.forEach(event => timeline.push(event));
    ignoreHistory.forEach(event => timeline.push({
      occurredAt: event.occurredAt,
      kind: event.action,
      detail: event.reason,
      actor: event.actor
    }));
    if (item.ignored && !ignoreHistory.length && item.ignoredAt) {
      timeline.push({ occurredAt: item.ignoredAt, kind: "Ignored", detail: item.ignoredReason, actor: item.ignoredBy });
    }
    timeline.sort((a, b) => new Date(b.occurredAt || 0) - new Date(a.occurredAt || 0));
    $("#document-timeline-count").textContent = String(timeline.length);
    $("#document-timeline").innerHTML = timeline.length
      ? timeline.map(event => `<li class="document-timeline-item ${detailEventTone(event.kind)}"><strong>${html(detailEventLabel(event.kind))}</strong><p>${html(event.detail || "Status aktualisiert.")}</p><footer><time>${html(detailTime(event.occurredAt))}</time><span>${html(event.actor || "System")}</span></footer></li>`).join("")
      : '<li class="detail-empty">Für diesen Beleg ist noch kein Verlauf gespeichert.</li>';
  }

  function showRowDetailPreview(item) {
    const frame = $("#document-detail-preview-frame");
    const empty = $("#document-preview-empty");
    const copy = $("#document-preview-empty-copy");
    frame.removeAttribute("src");
    if (item.id && item.pdfState === "linked") {
      frame.src = `/api/v1/documents/by-sap/${encodeURIComponent(item.direction)}/${encodeURIComponent(item.docEntry)}/pdf?sapKind=${encodeURIComponent(item.sapKind)}#toolbar=1&view=FitH`;
      frame.hidden = false;
      empty.hidden = true;
      return;
    }
    frame.hidden = true;
    empty.hidden = false;
    copy.textContent = item.pdfState === "sap-only"
      ? "SAP referenziert einen Anhang, die PDF ist jedoch noch nicht im geschützten NovaNein-Archiv verfügbar."
      : "Für diesen Beleg ist noch keine PDF in NovaNein hinterlegt.";
  }

  async function openRowDetails(item) {
    if (!item || !item.sapKind || item.docEntry <= 0) return;
    const detailKey = `${item.sapKind}:${item.docEntry}:${Date.now()}`;
    state.rowDetailKey = detailKey;
    $("#document-detail-title").textContent = `${item.documentType || "Beleg"} · SAP ${item.docNum || item.docEntry}`;
    $("#document-detail-subtitle").textContent = item.businessPartner || "Geschäftspartner nicht angegeben";
    $("#document-detail-facts").innerHTML = [
      detailFact("Belegart", item.documentType),
      detailFact("Lieferantenbeleg", item.direction === "incoming" ? item.invoiceNumber : "—"),
      detailFact("Belegdatum", formatDate(item.documentDate)),
      detailFact("Betrag", formatMoney(item.grossAmount, item.currency)),
      detailFact("Status", item.overallLabel || STATUS_TEXT[item.kind] || item.kind)
    ].join("");
    $("#document-comments-count").textContent = "…";
    $("#document-timeline-count").textContent = "…";
    $("#document-comments").innerHTML = '<div class="detail-loading">Kommentare werden geladen …</div>';
    $("#document-timeline").innerHTML = '<li class="detail-loading">Verlauf wird geladen …</li>';
    showRowDetailPreview(item);
    openDialog($("#document-detail-dialog"));

    const sapRequest = api.get(`/api/v1/sap/documents/${encodeURIComponent(item.sapKind)}/${encodeURIComponent(item.docEntry)}`).catch(() => null);
    const eventsRequest = item.id ? api.get(`/api/v1/documents/${encodeURIComponent(item.id)}/events`).catch(() => []) : Promise.resolve([]);
    const ignoreRequest = api.get(`/api/v1/work-items/${encodeURIComponent(item.sapKind)}/${encodeURIComponent(item.docEntry)}/ignore-history`).catch(() => []);
    const [sap, rawEvents, rawIgnoreHistory] = await Promise.all([sapRequest, eventsRequest, ignoreRequest]);
    if (state.rowDetailKey !== detailKey) return;
    const events = asArray(rawEvents);
    const ignoreHistory = asArray(rawIgnoreHistory);
    const comments = [];
    if (sap && String(sap.comments || "").trim()) comments.push({ source: "SAP-Kommentar", text: repairMojibake(sap.comments), actor: "SAP" });
    events.filter(event => COMMENT_EVENT_KINDS.has(event.kind) && event.detail).forEach(event => comments.push({ source: "NovaNein-Notiz", text: repairMojibake(event.detail), actor: repairMojibake(event.actor || ""), occurredAt: event.occurredAt }));
    ignoreHistory.filter(event => event.reason).forEach(event => comments.push({ source: event.action === "Restored" ? "Wiederherstellung" : "Ignorierung", text: repairMojibake(event.reason), actor: repairMojibake(event.actor || ""), occurredAt: event.occurredAt }));
    if (item.ignored && !ignoreHistory.length && item.ignoredReason) comments.push({ source: "Ignorierung", text: item.ignoredReason, actor: item.ignoredBy, occurredAt: item.ignoredAt });
    renderDetailComments(comments);
    renderDetailTimeline(item, events, ignoreHistory);
  }

  async function openInboxAssignment(item) {
    if (!item) return;
    state.dialogMode = "assign"; state.dialogItem = item;
    showPdfPreview(item);
    $("#review-dialog-title").textContent = "PDF einem SAP-Beleg zuordnen";
    $("#review-summary").innerHTML = `<strong>${html(item.fileName || "Unbenannte PDF")}</strong><span>Die Zuordnung wird vor dem Speichern nochmals in SAP geprüft.</span>`;
    $("#assign-target-wrap").hidden = false;
    populateAssignTargets();
    const suggestions = Array.isArray(item.suggestions) ? item.suggestions : [];
    $("#review-reasons").innerHTML = suggestions.length ? suggestions.map(suggestion => `<div class="reason-item">Vorschlag: ${html(suggestion.docNum || suggestion.documentNumber || "SAP-Beleg")} · ${html(suggestion.businessPartner || suggestion.businessPartnerName || "unbekannter Partner")}</div>`).join("") : '<div class="reason-item">Bitte wählen Sie den passenden SAP-Beleg aus.</div>';
    $("#review-reason").closest(".reason-field").hidden = true; $("#approve-button").hidden = false; $("#reject-button").hidden = true; $("#approve-button").textContent = "Zuordnung speichern"; $("#review-feedback").hidden = true;
    $("#replace-pdf-button").hidden = true;
    openDialog($("#review-dialog"));
    if (!suggestions.length && item.id) {
      try {
        const response = await api.get(`/api/v1/pdf-inbox/${encodeURIComponent(item.id)}/suggestions`);
        item.suggestions = asArray(response);
        const fresh = item.suggestions;
        $("#review-reasons").innerHTML = fresh.length ? fresh.map(suggestion => `<div class="reason-item">Vorschlag: ${html(suggestion.docNum || suggestion.documentNumber || "SAP-Beleg")} · ${html(suggestion.businessPartner || suggestion.businessPartnerName || "unbekannter Partner")}</div>`).join("") : '<div class="reason-item">Kein sicherer SAP-Vorschlag gefunden. Bitte wählen Sie den Beleg manuell.</div>';
      } catch { /* Die manuelle Auswahl bleibt verfügbar. */ }
    }
  }

  function hidePdfPreview() {
    const preview = $("#pdf-preview");
    const frame = $("#pdf-preview-frame");
    if (!preview || !frame) return;
    preview.hidden = true;
    frame.removeAttribute("src");
  }

  function showPdfPreview(item) {
    const preview = $("#pdf-preview");
    const frame = $("#pdf-preview-frame");
    if (!preview || !frame || !item || !item.id) return;
    frame.src = `/api/v1/pdf-inbox/${encodeURIComponent(item.id)}/file#toolbar=0&view=FitH`;
    preview.hidden = false;
  }

  function showDocumentPdfPreview(item) {
    const preview = $("#pdf-preview");
    const frame = $("#pdf-preview-frame");
    if (!preview || !frame || !item || !item.id) return;
    frame.src = `/api/v1/documents/by-sap/${encodeURIComponent(item.direction)}/${encodeURIComponent(item.docEntry)}/pdf?sapKind=${encodeURIComponent(item.sapKind)}#toolbar=1&view=FitH`;
    preview.hidden = false;
  }

  function openTransfer(item) {
    if (!canReviewDocuments()) { toast("Für eine DATEV-Übertragung fehlt die Berechtigung „Belege bearbeiten“.", "warning"); return; }
    if (!item || !item.id) { toast("Das DATEV-Paket ist noch nicht als Dokument gespeichert.", "warning"); return; }
    state.dialogMode = "transfer"; state.dialogItem = item;
    $("#transfer-dialog-title").textContent = "Übertragung bestätigen"; $("#transfer-confirm").checked = false; $("#transfer-submit").disabled = true; $("#transfer-feedback").hidden = true;
    $("#transfer-check").innerHTML = `${itemSummary(item)}<span>Der Upload wird erst nach Ihrer Bestätigung an den geschützten Transfer übergeben.</span>`;
    openDialog($("#transfer-dialog"));
    api.get(`/api/v1/documents/${encodeURIComponent(item.id)}/datev`).then(data => {
      if (!data) return;
      const hash = data.packageSha256 ? `<span>SHA-256: <code>${html(data.packageSha256)}</code></span>` : "<span>Prüfsumme wird noch erstellt.</span>";
      $("#transfer-check").insertAdjacentHTML("beforeend", hash);
    }).catch(() => { /* Der Dialog bleibt nutzbar; der Server prüft den Hash nochmals. */ });
  }

  function openDialog(dialog) {
    if (typeof dialog.showModal === "function") dialog.showModal(); else dialog.setAttribute("open", "");
  }

  async function submitReview(submitter) {
    const item = state.dialogItem;
    if (!item) return;
    if (submitter === "cancel") { $("#review-dialog").close(); return; }
    if (state.dialogMode === "details") {
      $("#review-dialog").close();
      toast("Die Detailansicht ist schreibgeschützt. Für eine Entscheidung benötigen Sie eine Reviewer-Sitzung.", "warning");
      return;
    }
    const feedback = $("#review-feedback");
    if (state.dialogMode === "assign") {
      const target = state.selectedTarget;
      if (!target) { feedback.hidden = false; feedback.dataset.tone = "error"; feedback.textContent = "Bitte zuerst im Upload-Bereich einen SAP-Beleg auswählen."; return; }
      try {
        await api.postJson(`/api/v1/pdf-inbox/${encodeURIComponent(item.id)}/assign`, { direction: target.direction, sapKind: target.sapKind, docEntry: target.docEntry, docNum: target.docNum });
        $("#review-dialog").close(); toast("Die PDF wurde dem SAP-Beleg zugeordnet."); await loadDashboard();
      } catch (error) { feedback.hidden = false; feedback.dataset.tone = "error"; feedback.textContent = userFacingError(error, "Die Zuordnung konnte nicht gespeichert werden."); }
      return;
    }
    const reason = $("#review-reason").value.trim();
    if (!reason) { feedback.hidden = false; feedback.dataset.tone = "error"; feedback.textContent = "Bitte geben Sie eine kurze Begründung ein."; return; }
    const approve = submitter === "approve";
    $$('button', $("#review-form")).forEach(button => button.disabled = true);
    try {
      if (state.dialogMode === "credit-note-release") {
        await api.postJson(`/api/v1/documents/${encodeURIComponent(item.id)}/credit-note-release`, { approve: true, reason });
        $("#review-dialog").close(); toast("Die Gutschrift wurde für DATEV freigegeben. Das Paket wird vorbereitet."); await loadDashboard();
      } else {
        await api.postJson(`/api/v1/documents/${encodeURIComponent(item.id)}/reviews`, { approve, reason });
        $("#review-dialog").close(); toast(approve ? "Beleg wurde freigegeben." : "Beleg wurde abgelehnt."); await loadDashboard();
      }
    } catch (error) {
      feedback.hidden = false; feedback.dataset.tone = "error"; feedback.textContent = userFacingError(error, "Die Entscheidung konnte nicht gespeichert werden.");
      $$('button', $("#review-form")).forEach(button => button.disabled = false);
    }
  }

  async function submitTransfer() {
    const item = state.dialogItem;
    if (!item || !$("#transfer-confirm").checked) return;
    const submit = $("#transfer-submit"); const feedback = $("#transfer-feedback"); submit.disabled = true; submit.textContent = "Wird eingestellt …";
    let packageSha256 = "";
    try {
      const datev = await api.get(`/api/v1/documents/${encodeURIComponent(item.id)}/datev`); packageSha256 = datev && datev.packageSha256 || "";
      await api.postJson(`/api/v1/documents/${encodeURIComponent(item.id)}/transfer-requests`, { packageSha256, confirm: true });
      $("#transfer-dialog").close(); toast("Übertragung wurde zur sicheren Übergabe eingestellt."); await loadDashboard();
    } catch (error) {
      feedback.hidden = false; feedback.dataset.tone = "error"; feedback.textContent = userFacingError(error, "Die Übertragung konnte nicht eingestellt werden."); submit.disabled = false; submit.textContent = "Übertragung anstoßen";
    }
  }

  function handleAction(action, item) {
    switch (action) {
      case "upload": openUploadDialog(item); break;
      case "review": openReview(item); break;
      case "credit-note-release": openCreditNoteRelease(item); break;
      case "prepare": prepareDatev(item); break;
      case "download": downloadDatev(item); break;
      case "transfer": openTransfer(item); break;
      case "retry": retryTransfer(item); break;
      case "ignore": openAdminIgnore(item, false); break;
      case "restore-ignore": openAdminIgnore(item, true); break;
      case "details":
      default: openRowDetails(item); break;
    }
  }

  function openAdminIgnore(item, restore) {
    if (!item || !item.sapKind || item.docEntry <= 0) {
      toast("Dieser SAP-Beleg kann nicht eindeutig zugeordnet werden.", "warning");
      return;
    }
    state.adminAction = { item, restore };
    $("#admin-ignore-title").textContent = restore ? "Ignorierung aufheben" : "Zeile ignorieren";
    $("#admin-ignore-copy").textContent = restore
      ? "Der Beleg wird wieder als offene Aufgabe geführt und in den Kennzahlen berücksichtigt."
      : "Der Beleg bleibt durchgestrichen sichtbar, wird aber nicht mehr als offen gezählt.";
    $("#admin-ignore-target").innerHTML = `<strong>${html(item.documentType)} · SAP ${html(item.docNum || item.docEntry)}</strong><span>${html(item.businessPartner)}</span>${item.ignoredReason ? `<small>Bisherige Begründung: ${html(item.ignoredReason)}</small>` : ""}`;
    $("#admin-ignore-password").value = "";
    $("#admin-ignore-reason").value = "";
    $("#admin-ignore-reason").placeholder = restore ? "Warum soll der Beleg wieder geöffnet werden?" : "Zum Beispiel: Beleg wurde in SAP storniert";
    $("#admin-ignore-submit").textContent = restore ? "Wieder als offen führen" : "Zeile ignorieren";
    $("#admin-ignore-submit").classList.toggle("button-danger", !restore);
    $("#admin-ignore-submit").classList.toggle("button-primary", restore);
    $("#admin-ignore-feedback").hidden = true;
    openDialog($("#admin-ignore-dialog"));
    window.setTimeout(() => $("#admin-ignore-user").focus(), 0);
  }

  async function submitAdminIgnore() {
    const action = state.adminAction;
    if (!action || !action.item) return;
    const userName = $("#admin-ignore-user").value.trim();
    const password = $("#admin-ignore-password").value;
    const reason = $("#admin-ignore-reason").value.trim();
    const feedback = $("#admin-ignore-feedback");
    if (!userName || !password) {
      feedback.hidden = false;
      feedback.textContent = "Bitte Admin-Benutzername und Admin-Kennwort eingeben.";
      return;
    }
    if (reason.length < 3) {
      feedback.hidden = false;
      feedback.textContent = "Bitte geben Sie eine kurze Begründung ein.";
      return;
    }
    const submit = $("#admin-ignore-submit");
    submit.disabled = true;
    try {
      const item = action.item;
      const operation = action.restore ? "restore" : "ignore";
      await api.postJson(`/api/v1/work-items/${encodeURIComponent(item.sapKind)}/${encodeURIComponent(item.docEntry)}/${operation}`, {
        adminUserName: userName,
        adminPassword: password,
        reason,
        docNum: item.docNum
      });
      $("#admin-ignore-dialog").close();
      $("#admin-ignore-password").value = "";
      state.adminAction = null;
      toast(action.restore ? "Die Zeile wird wieder als offen geführt." : "Die Zeile wurde ignoriert und aus den offenen Kennzahlen entfernt.");
      await loadDashboard();
    } catch (error) {
      feedback.hidden = false;
      feedback.textContent = userFacingError(error, action.restore ? "Die Ignorierung konnte nicht aufgehoben werden." : "Die Zeile konnte nicht ignoriert werden.");
    } finally {
      submit.disabled = false;
    }
  }

  function downloadDatev(item) {
    if (!item || !item.id) return;
    const link = document.createElement("a");
    link.href = apiUrl(`/api/v1/documents/${encodeURIComponent(item.id)}/datev/package`);
    link.rel = "noopener";
    document.body.appendChild(link);
    link.click();
    link.remove();
  }

  async function retryTransfer(item) {
    if (!item || !item.id) return;
    try {
      await api.postJson(`/api/v1/documents/${encodeURIComponent(item.id)}/transfer-requests/retry`, {});
      toast("Der DATEV-Transfer wurde zur erneuten Übergabe eingestellt.");
      await loadDashboard();
    } catch (error) {
      toast(userFacingError(error, "Der DATEV-Transfer konnte nicht erneut eingestellt werden."), "error");
    }
  }

  async function prepareDatev(item) {
    if (!item || !item.id) return;
    try {
      await api.postJson(`/api/v1/documents/${encodeURIComponent(item.id)}/datev/package`, {});
      toast("Das DATEV-Paket wird automatisch vorbereitet.");
      await loadDashboard();
    } catch (error) {
      toast(userFacingError(error, "Die DATEV-Paketvorbereitung konnte nicht gestartet werden."), "error");
    }
  }

  async function loadDashboard() {
    if (state.loading) return;
    state.loading = true; render();
    const from = new Date();
    if (state.dateDays !== "all") from.setDate(from.getDate() - number(state.dateDays, 90));
    const to = new Date();
    const fromDate = state.dateDays === "all" ? "" : localDateString(from);
    const toDate = localDateString(to);
    // Keep both names during the API transition: the current Minimal API uses
    // fromEntryDate/toEntryDate, while the planned cockpit contract uses from/to.
    const query = buildWorkQuery(fromDate, toDate, 1);
    try {
      const [workResult, inboxResult, statsResult, healthResult] = await Promise.allSettled([
        api.get(`/api/v1/work-items${query}`),
        api.get("/api/v1/pdf-inbox"),
        api.get(`/api/v1/work-items/summary?fromEntryDate=${encodeURIComponent(fromDate)}&toEntryDate=${encodeURIComponent(toDate)}${state.direction !== "all" ? `&direction=${encodeURIComponent(state.direction)}` : ""}`),
        api.get("/health", { timeoutMs: 8_000 })
      ]);
      let workPayload = workResult.status === "fulfilled" ? workResult.value : null;
      let workSucceeded = workResult.status === "fulfilled";
      if (workResult.status === "rejected" && workResult.reason instanceof ApiError && workResult.reason.status === 404) {
        state.hasWorkItemsEndpoint = false;
        try {
          const fallback = await api.get(`/api/v1/scans/missing-pdf?fromEntryDate=${encodeURIComponent(fromDate || "1900-01-01")}&toEntryDate=${encodeURIComponent(toDate)}`);
          workPayload = asArray(fallback).map(item => ({ ...item, direction: normalizeDirection(item.kind), pdfState: "missing", nextAction: { key: "upload", label: "PDF hochladen" } }));
          workSucceeded = true;
        } catch (error) {
          if (!(error instanceof ApiError && error.status === 404)) throw error;
          workPayload = [];
          workSucceeded = true;
        }
      }
      if (workResult.status === "rejected" && workResult.reason instanceof ApiError && [401, 403].includes(workResult.reason.status)) throw workResult.reason;
      if (workSucceeded) {
        state.nextCursor = workPayload && !Array.isArray(workPayload) ? (workPayload.nextPage || workPayload.nextCursor || workPayload.next?.page || workPayload.next?.cursor || null) : null;
        state.items = asArray(workPayload).map(normalizeWorkItem);
        state.hasCompleteUploadTargets = Boolean(workPayload && !Array.isArray(workPayload) && Array.isArray(workPayload.uploadTargets));
        state.uploadTargets = state.hasCompleteUploadTargets
          ? workPayload.uploadTargets.map(normalizeWorkItem)
          : state.items.filter(item => item.docEntry > 0 && (item.kind === "missing" || item.pdfState === "missing" || !item.id));
        state.workListError = null;
      } else {
        state.nextCursor = null;
        state.workListError = userFacingError(workResult.reason, "Die Aufgabenliste ist momentan nicht verfügbar.");
      }
      state.inbox = inboxResult.status === "fulfilled" ? asArray(inboxResult.value).map(item => ({ id: item.id, fileName: item.fileName || item.originalFileName || "Unbenannte PDF", createdAt: item.createdAt, suggestions: item.suggestions || [] })) : [];
      state.stats = statsResult.status === "fulfilled" ? normalizeStats(statsResult.value) : {};
      state.health = healthResult.status === "fulfilled" ? healthResult.value : null;
      if (state.health && !state.healthNoticeShown && state.health.datevXsds === "required-before-package-generation") {
        showNotice("DATEV-Pakete bleiben gesperrt, bis die originalen DATEV-XSDs auf dem Server hinterlegt sind.", "warning", true);
        state.healthNoticeShown = true;
      }
      // Session details are optional during mTLS-only rollout. A missing endpoint
      // must not prevent the operational list from loading. The current server
      // exposes /auth/me; /api/v1/session remains a compatible future hook.
      try { updateSession(await api.get("/auth/me", { timeoutMs: 5_000 })); }
      catch { try { updateSession(await api.get("/api/v1/session", { timeoutMs: 5_000 })); } catch { /* optional */ } }
      state.lastUpdated = new Date();
      setConnection(workSucceeded ? "ok" : "error", workSucceeded ? "Live verbunden" : "Aufgabenliste nicht verfügbar");
      $("#last-sync").textContent = `Zuletzt aktualisiert: ${formatDateTime(state.lastUpdated)}`;
      if (!workSucceeded) showNotice(state.workListError, "error", true);
    } catch (error) {
      state.items = state.items || [];
      setConnection(error instanceof ApiError && [401, 403].includes(error.status) ? "error" : "error", error instanceof ApiError && [401, 403].includes(error.status) ? "Anmeldung oder Arbeitsplatz-Zertifikat prüfen" : "Verbindung nicht verfügbar");
      showNotice(userFacingError(error, "Der Live-Zustand konnte nicht geladen werden."), "error", true);
    } finally {
      state.loading = false; render();
      if (state.pollTimer) window.clearTimeout(state.pollTimer);
      state.pollTimer = window.setTimeout(() => { if (!document.hidden) loadDashboard(); }, POLL_INTERVAL_MS);
    }
  }

  async function loadMore() {
    if (!state.nextCursor || state.loading) return;
    const button = $("#load-more");
    button.disabled = true; button.textContent = "Wird geladen …";
    try {
      const from = new Date();
      if (state.dateDays !== "all") from.setDate(from.getDate() - number(state.dateDays, 90));
      const fromDate = state.dateDays === "all" ? "" : localDateString(from);
      const toDate = localDateString(new Date());
      const payload = await api.get(`/api/v1/work-items${buildWorkQuery(fromDate, toDate, state.nextCursor)}`);
      state.items = state.items.concat(asArray(payload).map(normalizeWorkItem));
      state.nextCursor = payload && !Array.isArray(payload) ? (payload.nextPage || payload.nextCursor || payload.next?.page || payload.next?.cursor || null) : null;
      render();
    } catch (error) { toast(userFacingError(error, "Weitere Belege konnten nicht geladen werden."), "error"); }
    finally { button.disabled = false; button.textContent = "Weitere laden"; }
  }

  function buildWorkQuery(fromDate, toDate, page) {
    const params = new URLSearchParams();
    params.set("fromEntryDate", fromDate); params.set("toEntryDate", toDate);
    params.set("from", fromDate); params.set("to", toDate);
    params.set("page", String(page || 1)); params.set("pageSize", "50");
    if (state.direction !== "all") params.set("direction", state.direction);
    if (state.filter === "missing") params.set("pdfPresent", "false");
    if (state.filter === "review") params.set("status", "manual-review");
    if (state.filter === "ready") params.set("datevStatus", "ready");
    if (state.filter === "error") params.set("errorStatus", "true");
    if (state.search.trim()) params.set("search", state.search.trim());
    params.set("sortBy", state.sortBy);
    params.set("sortDirection", state.sortDirection);
    return `?${params.toString()}`;
  }

  function activateSort(field) {
    if (!field) return;
    if (state.sortBy === field) state.sortDirection = state.sortDirection === "asc" ? "desc" : "asc";
    else {
      state.sortBy = field;
      state.sortDirection = "asc";
    }
    saveViewPreferences();
    state.page = 1;
    loadDashboard();
  }

  function bindEvents() {
    $("#refresh-button").addEventListener("click", () => { loadDashboard(); });
    let searchTimer = null;
    $("#worklist-search").addEventListener("input", event => {
      state.search = event.target.value;
      renderWorklist();
      if (searchTimer) window.clearTimeout(searchTimer);
      searchTimer = window.setTimeout(() => {
        searchTimer = null;
        loadDashboard();
      }, 300);
    });
    let sortClickTimer = null;
    $$('[data-sort-column]').forEach(header => {
      const button = $(".sort-button", header);
      header.title = "Doppelklick zum Sortieren";
      header.addEventListener("dblclick", event => {
        event.preventDefault();
        if (sortClickTimer) clearTimeout(sortClickTimer);
        sortClickTimer = null;
        activateSort(header.dataset.sortColumn);
      });
      if (!button) return;
      button.addEventListener("click", event => {
        if (event.detail === 0) {
          activateSort(button.dataset.sort);
          return;
        }
        if (sortClickTimer) clearTimeout(sortClickTimer);
        sortClickTimer = setTimeout(() => {
          sortClickTimer = null;
          activateSort(button.dataset.sort);
        }, 260);
      });
    });
    $("#direction-filter").addEventListener("change", event => { state.direction = event.target.value; saveViewPreferences(); loadDashboard(); });
    $("#date-filter").addEventListener("change", event => { state.dateDays = event.target.value; saveViewPreferences(); loadDashboard(); });
    $$(".metric").forEach(button => button.addEventListener("click", () => { state.filter = state.filter === button.dataset.filter ? "all" : button.dataset.filter; saveViewPreferences(); loadDashboard(); }));
    $("#clear-filter").addEventListener("click", () => { state.filter = "all"; saveViewPreferences(); loadDashboard(); });
    $("#show-all-button").addEventListener("click", () => { state.filter = "all"; saveViewPreferences(); loadDashboard(); });
    $("#load-more").addEventListener("click", loadMore);
    const openUploadButton = $("#open-upload-dialog");
    if (openUploadButton) openUploadButton.addEventListener("click", () => { state.selectedTarget = null; openUploadDialog(); });
    $("#upload-dialog-close").addEventListener("click", () => $("#upload-dialog").close());
    $("#pdf-input").addEventListener("change", event => setFile(event.target.files[0]));
    $("#dropzone").addEventListener("keydown", event => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); $("#pdf-input").click(); } });
    const dropzone = $("#dropzone");
    ["dragenter", "dragover"].forEach(name => dropzone.addEventListener(name, event => { event.preventDefault(); dropzone.dataset.dragging = "true"; }));
    ["dragleave", "drop"].forEach(name => dropzone.addEventListener(name, event => { event.preventDefault(); dropzone.dataset.dragging = "false"; }));
    dropzone.addEventListener("drop", event => setFile(event.dataTransfer.files[0]));
    $("#clear-file").addEventListener("click", () => { state.selectedFile = null; if (!state.replacementMode) state.selectedTarget = null; $("#pdf-input").value = ""; showUploadFeedback(""); renderUpload(); });
    $("#upload-target-select").addEventListener("change", event => { state.selectedTarget = parseTarget(event.target.value); $("#upload-button").disabled = !(state.selectedFile && state.selectedTarget); });
    $("#review-target-select").addEventListener("change", event => { state.selectedTarget = parseTarget(event.target.value); });
    $("#upload-button").addEventListener("click", uploadSelectedFile);
    $("#upload-inbox-button").addEventListener("click", uploadToInbox);
    $("#review-form").addEventListener("submit", event => { event.preventDefault(); submitReview(event.submitter && event.submitter.value); });
    $("#replace-pdf-button").addEventListener("click", () => { const item = state.dialogItem; if (!mayReplacePdf(item)) return; $("#review-dialog").close(); openUploadDialog(item, true); });
    $("#transfer-confirm").addEventListener("change", event => { $("#transfer-submit").disabled = !event.target.checked; });
    $("#transfer-form").addEventListener("submit", event => { event.preventDefault(); if (event.submitter && event.submitter.value === "transfer") submitTransfer(); else $("#transfer-dialog").close(); });
    $("#admin-ignore-form").addEventListener("submit", event => { event.preventDefault(); if (event.submitter && event.submitter.value === "confirm") submitAdminIgnore(); else $("#admin-ignore-dialog").close(); });
    $("#document-detail-dialog").addEventListener("close", () => { state.rowDetailKey = null; $("#document-detail-preview-frame").removeAttribute("src"); });
    window.addEventListener("online", () => { setConnection("loading", "Verbindung wird geprüft …"); loadDashboard(); });
    document.addEventListener("visibilitychange", () => { if (!document.hidden) setConnection("ok", "Live verbunden"); });
  }

  async function ensureCsrf() {
    try {
      const response = await fetch("/auth/csrf", { credentials: "include", headers: { Accept: "application/json" } });
      if (!response.ok) return;
      const payload = await response.json();
      const meta = document.querySelector('meta[name="csrf-token"]');
      if (meta && payload && payload.token) meta.content = payload.token;
    } catch { /* The next API request will surface the connection problem. */ }
  }

  async function connectLiveUpdates() {
    if (window.location.protocol !== "https:" || !window.signalR) return;
    const token = await ensureCsrfToken();
    const connection = new window.signalR.HubConnectionBuilder().withUrl("/hubs/status", { headers: { "X-CSRF-Token": token } }).withAutomaticReconnect().build();
    connection.on("statusChanged", () => { setConnection("ok", "Live verbunden"); });
    connection.onreconnected(() => setConnection("ok", "Live verbunden"));
    connection.onclose(() => { setConnection("loading", "Live-Verbindung wird wiederhergestellt"); });
    try { await connection.start(); } catch { /* Das 15-Minuten-Polling bleibt als sichere Rückfallebene aktiv. */ }
  }

  async function boot() {
    await ensureCsrf();
    restoreViewPreferences();
    const initialSearch = new URLSearchParams(window.location.search).get("search");
    if (initialSearch) {
      state.filter = "all";
      state.direction = "all";
      state.search = initialSearch.slice(0, 120);
      $("#worklist-search").value = state.search;
    }
    applyViewPreferencesToControls();
    bindEvents(); render(); await connectLiveUpdates(); loadDashboard();
  }

  window.NovaNeinDashboard = { refresh: loadDashboard, state };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot); else boot();
}());
