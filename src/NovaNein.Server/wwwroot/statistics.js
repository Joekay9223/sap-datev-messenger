(function () {
  "use strict";

  const state = { overview: null, view: "uploads", periodKey: "today" };
  const $ = selector => document.querySelector(selector);

  async function api(path, options = {}) {
    const response = await fetch(path, {
      credentials: "include",
      ...options,
      headers: { Accept: "application/json", "X-Requested-With": "NovaNein-Web", ...(options.headers || {}) }
    });
    if (response.status === 401) {
      window.location.assign("/login.html");
      throw new Error("Anmeldung erforderlich.");
    }
    const payload = response.status === 204 ? null : await response.json().catch(() => null);
    if (!response.ok) throw new Error(payload?.detail || payload?.error || "Die Statistik konnte nicht geladen werden.");
    return payload;
  }

  function html(value) {
    const node = document.createElement("div");
    node.textContent = value ?? "";
    return node.innerHTML;
  }

  function number(value, digits = 0) {
    return Number(value || 0).toLocaleString("de-DE", { minimumFractionDigits: digits, maximumFractionDigits: digits });
  }

  function money(value, currency = "EUR") {
    return Number(value || 0).toLocaleString("de-DE", { style: "currency", currency: currency || "EUR" });
  }

  function date(value) {
    if (!value) return "–";
    return new Intl.DateTimeFormat("de-DE", { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(`${value}T12:00:00`));
  }

  function activePeriod() {
    return state.overview?.periods?.find(period => period.key === state.periodKey) || state.overview?.periods?.[0] || null;
  }

  function dayCount(period) {
    if (!period) return 1;
    const from = new Date(`${period.from}T12:00:00Z`);
    const to = new Date(`${period.to}T12:00:00Z`);
    return Math.max(1, Math.round((to - from) / 86_400_000) + 1);
  }

  function renderPeriodButtons() {
    const periods = state.overview?.periods || [];
    $("#period-buttons").innerHTML = periods.map(period => `
      <button class="period-button" type="button" data-period="${html(period.key)}" aria-pressed="${period.key === state.periodKey}">${html(period.label)}</button>`).join("");
    document.querySelectorAll("[data-period]").forEach(button => {
      button.addEventListener("click", () => { state.periodKey = button.dataset.period; render(); });
    });
  }

  function renderTabs() {
    document.querySelectorAll("[data-view]").forEach(button => {
      const active = button.dataset.view === state.view;
      button.setAttribute("aria-selected", String(active));
      button.tabIndex = active ? 0 : -1;
    });
  }

  function metric(label, value, hint, tone = "") {
    return `<div class="metric ${tone}"><span class="metric-label">${html(label)}</span><strong>${html(value)}</strong><span class="metric-hint">${html(hint)}</span></div>`;
  }

  function renderUploadMetrics(period) {
    const uploads = period.uploads || {};
    const average = Number(uploads.total || 0) / dayCount(period);
    $("#statistics-metrics").innerHTML = [
      metric("PDF-Uploads", number(uploads.total), "Im gewählten Zeitraum"),
      metric("Eingangsbelege", number(uploads.incoming), "Hochgeladene Eingangs-PDFs"),
      metric("Ausgangsbelege", number(uploads.outgoing), "Hochgeladene Ausgangs-PDFs"),
      metric("Ø pro Tag", number(average, 1), "Kalendertäglicher Durchschnitt")
    ].join("");
  }

  function renderRevenueMetrics(period) {
    const revenue = period.revenue || {};
    const currencies = revenue.currencies || [];
    const primary = currencies.length === 1 ? currencies[0] : null;
    $("#statistics-metrics").innerHTML = [
      metric("Nettoumsatz", primary ? money(primary.netRevenue, primary.currency) : currencies.length ? `${currencies.length} Währungen` : money(0), "Rechnungen abzüglich Gutschriften"),
      metric("Ausgangsrechnungen", number(revenue.invoiceCount), "In SAP angelegte Belege"),
      metric("Ausgangsgutschriften", number(revenue.creditNoteCount), "Umsatzmindernde Belege", revenue.creditNoteCount ? "metric-attention" : ""),
      metric("Brutto fakturiert", primary ? money(primary.grossInvoiced, primary.currency) : currencies.length ? "Siehe Details" : money(0), "Vor Abzug der Gutschriften")
    ].join("");
  }

  function renderUploadDetail(period) {
    const uploads = period.uploads || {};
    const maximum = Math.max(1, Number(uploads.incoming || 0), Number(uploads.outgoing || 0));
    $("#detail-kicker").textContent = "Uploadstatistik";
    $("#detail-title").textContent = `PDF-Eingänge · ${period.label}`;
    $("#statistics-detail").innerHTML = `
      <div class="stat-bars">
        ${uploadBar("Eingangsbelege", "Lieferantenrechnungen und -gutschriften", uploads.incoming, maximum)}
        ${uploadBar("Ausgangsbelege", "Kundenrechnungen und -gutschriften", uploads.outgoing, maximum)}
      </div>`;
  }

  function uploadBar(label, hint, value, maximum) {
    const width = Math.max(0, Math.min(100, Number(value || 0) / maximum * 100));
    return `<div class="stat-bar-row"><div class="stat-bar-label"><strong>${html(label)}</strong><small>${html(hint)}</small></div><div class="stat-bar-track"><span class="stat-bar-fill" style="width:${width}%"></span></div><strong class="stat-bar-value">${number(value)}</strong></div>`;
  }

  function renderRevenueDetail(period) {
    const currencies = period.revenue?.currencies || [];
    $("#detail-kicker").textContent = "Umsatzstatistik";
    $("#detail-title").textContent = `SAP-Umsatz · ${period.label}`;
    if (!currencies.length) {
      $("#statistics-detail").innerHTML = '<div class="statistics-empty"><div><strong>Kein Umsatz in diesem Zeitraum</strong><p>Es wurden keine Ausgangsrechnungen oder Ausgangsgutschriften angelegt.</p></div></div>';
      return;
    }
    $("#statistics-detail").innerHTML = `<div class="currency-list">${currencies.map(currency => `
      <article class="currency-card">
        <div class="currency-main"><span>Nettoumsatz · ${html(currency.currency)}</span><strong>${html(money(currency.netRevenue, currency.currency))}</strong></div>
        <div class="currency-value"><span>Fakturiert</span><strong>${html(money(currency.grossInvoiced, currency.currency))}</strong></div>
        <div class="currency-value"><span>Gutschriften</span><strong>${html(money(currency.grossCredited, currency.currency))}</strong></div>
        <div class="currency-value"><span>Belege</span><strong>${number(currency.invoiceCount)} / ${number(currency.creditNoteCount)}</strong></div>
      </article>`).join("")}</div>`;
  }

  function revenueText(period) {
    const currencies = period.revenue?.currencies || [];
    if (!currencies.length) return money(0);
    return currencies.map(value => money(value.netRevenue, value.currency)).join(" · ");
  }

  function renderComparison() {
    const periods = state.overview?.periods || [];
    if (state.view === "uploads") {
      $("#comparison-head").innerHTML = "<tr><th>Zeitraum</th><th>Gesamt</th><th>Eingang</th><th>Ausgang</th></tr>";
      $("#comparison-body").innerHTML = periods.map(period => `<tr data-active="${period.key === state.periodKey}"><td><strong>${html(period.label)}</strong><small>${date(period.from)}${period.from === period.to ? "" : ` – ${date(period.to)}`}</small></td><td>${number(period.uploads?.total)}</td><td>${number(period.uploads?.incoming)}</td><td>${number(period.uploads?.outgoing)}</td></tr>`).join("");
    } else {
      $("#comparison-head").innerHTML = "<tr><th>Zeitraum</th><th>Rechnungen</th><th>Gutschriften</th><th>Nettoumsatz</th></tr>";
      $("#comparison-body").innerHTML = periods.map(period => `<tr data-active="${period.key === state.periodKey}"><td><strong>${html(period.label)}</strong><small>${date(period.from)}${period.from === period.to ? "" : ` – ${date(period.to)}`}</small></td><td>${number(period.revenue?.invoiceCount)}</td><td>${number(period.revenue?.creditNoteCount)}</td><td>${html(revenueText(period))}</td></tr>`).join("");
    }
  }

  function render() {
    const period = activePeriod();
    if (!period) return;
    renderTabs();
    renderPeriodButtons();
    $("#selected-period-label").textContent = period.label;
    $("#selected-period-range").textContent = period.from === period.to ? date(period.from) : `${date(period.from)} bis ${date(period.to)}`;
    if (state.view === "uploads") {
      renderUploadMetrics(period);
      renderUploadDetail(period);
    } else {
      renderRevenueMetrics(period);
      renderRevenueDetail(period);
    }
    renderComparison();
  }

  async function load() {
    try {
      state.overview = await api("/api/v1/statistics/overview");
      $("#statistics-error").hidden = true;
      $("#live-state").textContent = `Stand ${new Date(state.overview.generatedAt).toLocaleString("de-DE")}`;
      render();
    } catch (error) {
      $("#statistics-error").hidden = false;
      $("#statistics-error").textContent = error.message;
      $("#live-state").textContent = "Statistik nicht verfügbar";
    }
  }

  document.addEventListener("DOMContentLoaded", () => {
    document.querySelectorAll("[data-view]").forEach(button => {
      button.addEventListener("click", () => { state.view = button.dataset.view; render(); });
    });
    load();
  });
}());
