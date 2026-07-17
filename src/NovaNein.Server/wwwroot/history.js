/* global fetch */
(function () {
  "use strict";

  const $ = selector => document.querySelector(selector);
  const ACTIONS = {
    DocumentReceived: ["PDF hochgeladen", "neutral"],
    ValidationCompleted: ["Prüfung abgeschlossen", "success"],
    ValidationRetryRequested: ["Prüfung neu gestartet", "review"],
    ManualReviewApproved: ["Manuell freigegeben", "success"],
    ManualReviewRejected: ["Manuell abgelehnt", "error"],
    PdfReplaced: ["PDF ausgetauscht", "review"],
    DatevPackagePrepared: ["DATEV-Paket vorbereitet", "success"],
    DatevTransferQueued: ["DATEV-Transfer eingestellt", "neutral"],
    DatevTransferCompleted: ["DATEV-Transfer abgeschlossen", "success"],
    TransferCompleted: ["DATEV-Transfer abgeschlossen", "success"],
    SapAttachmentVerified: ["SAP-Anhang bestätigt", "success"],
    ProcessingFailed: ["Verarbeitung fehlgeschlagen", "error"]
  };
  const STATUSES = {
    0: ["Eingegangen", "neutral"], 1: ["Prüfung läuft", "neutral"], 2: ["Prüfung nötig", "review"],
    3: ["Abgelehnt", "error"], 4: ["Freigegeben", "success"], 5: ["SAP bestätigt", "success"],
    6: ["DATEV bereit", "success"], 7: ["Übertragen", "success"], 8: ["Fehler", "error"]
  };
  const DOCUMENT_TYPES = { 1: "Eingangsrechnung", 2: "Ausgangsrechnung", 3: "Eingangsgutschrift", 4: "Ausgangsgutschrift" };

  function escapeHtml(value) { return String(value ?? "").replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[character])); }
  function formatTime(value) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return { date: "—", time: "" };
    return {
      date: new Intl.DateTimeFormat("de-DE", { day: "2-digit", month: "2-digit", year: "numeric" }).format(date),
      time: new Intl.DateTimeFormat("de-DE", { hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(date)
    };
  }
  function actionLabel(kind) {
    if (ACTIONS[kind]) return ACTIONS[kind];
    return [String(kind || "Aktion").replace(/([a-z])([A-Z])/g, "$1 $2"), "neutral"];
  }
  function statusLabel(status) { return STATUSES[Number(status)] || ["In Bearbeitung", "neutral"]; }
  function documentType(item) {
    const kind = Number(item.sapKind);
    if (DOCUMENT_TYPES[kind]) return DOCUMENT_TYPES[kind];
    return Number(item.direction) === 1 ? "Ausgangsbeleg" : "Eingangsbeleg";
  }
  function setConnection(state, label) { $("#history-connection").dataset.state = state; $("#history-connection-label").textContent = label; }

  function render(items) {
    const body = $("#history-body");
    $("#history-count").textContent = String(items.length);
    $("#history-empty").hidden = items.length > 0;
    body.innerHTML = items.map(item => {
      const time = formatTime(item.occurredAt);
      const [action, actionTone] = actionLabel(item.kind);
      const [status, statusTone] = statusLabel(item.currentStatus);
      const search = encodeURIComponent(String(item.docNum || item.docEntry || ""));
      return `<tr>
        <td><span class="history-time">${escapeHtml(time.date)}<small>${escapeHtml(time.time)}</small></span></td>
        <td><a class="history-document" href="/index.html?search=${search}"><strong>SAP ${escapeHtml(item.docNum || item.docEntry || "—")}</strong><small>${escapeHtml(documentType(item))}</small></a></td>
        <td><span class="history-action" data-tone="${escapeHtml(actionTone)}">${escapeHtml(action)}</span></td>
        <td class="history-detail">${escapeHtml(item.detail || "—")}</td>
        <td class="history-actor">${escapeHtml(item.actor || "System")}</td>
        <td><span class="history-status" data-tone="${escapeHtml(statusTone)}">${escapeHtml(status)}</span></td>
      </tr>`;
    }).join("");
  }

  async function loadHistory() {
    const button = $("#history-refresh");
    const feedback = $("#history-feedback");
    button.disabled = true; button.textContent = "Wird geladen …"; feedback.hidden = true;
    setConnection("loading", "Wird geladen …");
    try {
      const response = await fetch("/api/v1/activity?limit=50", { credentials: "include", headers: { Accept: "application/json", "X-Requested-With": "NovaNein-Web" } });
      if (!response.ok) throw new Error(`Serverantwort ${response.status}`);
      const payload = await response.json();
      render(Array.isArray(payload) ? payload : []);
      $("#history-updated").textContent = `Aktualisiert: ${new Intl.DateTimeFormat("de-DE", { dateStyle: "short", timeStyle: "short" }).format(new Date())}`;
      setConnection("ok", "Live verbunden");
    } catch (error) {
      feedback.hidden = false; feedback.textContent = "Die Historie konnte nicht geladen werden. Bitte Serververbindung prüfen und erneut versuchen.";
      setConnection("error", "Nicht verfügbar");
    } finally { button.disabled = false; button.textContent = "Aktualisieren"; }
  }

  $("#history-refresh").addEventListener("click", loadHistory);
  loadHistory();
}());
