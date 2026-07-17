/* global fetch */
(function () {
  "use strict";

  const REFRESH_INTERVAL_MS = 5 * 60_000;
  const OPEN_PROPOSAL_STATUSES = new Set(["MailReceived", "Extracted", "ProposalReady", "NeedsReview", "Blocked", "Approved", "SapPosting", "Failed"]);
  let refreshTimer = null;
  let identity = null;

  function renderCount(name, value) {
    const count = Math.max(0, Number(value) || 0);
    document.querySelectorAll(`[data-nav-count="${name}"]`).forEach(badge => {
      badge.textContent = count > 999 ? "999+" : String(count);
      badge.dataset.empty = String(count === 0);
      const itemLabel = name === "work-items" ? "offene Belege" : "offene Buchungsvorschläge";
      badge.setAttribute("aria-label", `${count} ${itemLabel}`);
      badge.closest(".nav-button")?.setAttribute("title", `${count} ${itemLabel}`);
    });
  }

  async function fetchJson(path, options = {}) {
    const response = await fetch(path, {
      credentials: "include",
      ...options,
      headers: { Accept: "application/json", "X-Requested-With": "NovaNein-Web", ...(options.headers || {}) }
    });
    if (!response.ok) throw new Error(`Serverantwort ${response.status}`);
    return response.status === 204 ? null : response.json();
  }

  function hasPermission(permission) {
    return identity?.role === "Manager" || identity?.role === "Admin" || (identity?.permissions || []).includes(permission);
  }

  function ensureUserMenu() {
    const actions = document.querySelector(".topbar-actions");
    if (!actions) return;
    const oldLogout = actions.querySelector("#logout");
    if (oldLogout) {
      oldLogout.hidden = true;
      oldLogout.setAttribute("aria-hidden", "true");
      oldLogout.tabIndex = -1;
    }
    if (!document.querySelector("#user-menu")) {
      actions.insertAdjacentHTML("beforeend", `
        <button class="user-menu" id="user-menu" type="button" aria-label="Benutzermenü öffnen" aria-expanded="false">
          <span class="avatar" id="user-avatar">?</span>
          <span class="user-menu-copy"><strong id="user-name">Arbeitsplatz</strong><small id="user-role">Sitzung</small></span>
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 9 6 6 6-6"/></svg>
        </button>
        <div class="user-popover" id="user-popover" hidden>
          <p class="popover-label">Angemeldet als</p>
          <strong id="popover-user">Arbeitsplatz</strong>
          <span id="popover-role">Sitzung</span>
          <a class="popover-link" id="user-administration-link" href="/admin-users.html" hidden>Benutzer und Rechte</a>
          <a class="popover-link" id="change-password-link" href="/change-password.html">Kennwort ändern</a>
          <button class="text-button" id="session-help" type="button">Sitzung prüfen</button>
          <button class="text-button" id="logout-button" type="button">Abmelden</button>
        </div>`);
    } else {
      const popover = document.querySelector("#user-popover");
      if (popover && !document.querySelector("#user-administration-link")) {
        const sessionHelp = document.querySelector("#session-help");
        sessionHelp?.insertAdjacentHTML("beforebegin", `
          <a class="popover-link" id="user-administration-link" href="/admin-users.html" hidden>Benutzer und Rechte</a>
          <a class="popover-link" id="change-password-link" href="/change-password.html">Kennwort ändern</a>`);
      }
    }
    bindUserMenu();
  }

  function bindUserMenu() {
    const menu = document.querySelector("#user-menu");
    const popover = document.querySelector("#user-popover");
    if (!menu || !popover || menu.dataset.globalBound === "true") return;
    menu.dataset.globalBound = "true";
    menu.addEventListener("click", event => {
      event.stopPropagation();
      const open = popover.hidden;
      popover.hidden = !open;
      menu.setAttribute("aria-expanded", String(open));
    });
    document.addEventListener("click", event => {
      if (!popover.hidden && !popover.contains(event.target) && !menu.contains(event.target)) {
        popover.hidden = true;
        menu.setAttribute("aria-expanded", "false");
      }
    });
    document.querySelector("#session-help")?.addEventListener("click", () => {
      window.alert("NovaNein protokolliert alle fachlichen Aktionen mit dem aktuell angemeldeten Benutzer.");
    });
    document.querySelector("#logout-button")?.addEventListener("click", logout);
  }

  async function csrfToken() {
    const payload = await fetchJson("/auth/csrf");
    return payload?.token || "";
  }

  async function logout() {
    try {
      const token = await csrfToken();
      await fetchJson("/auth/logout", { method: "POST", headers: { "X-CSRF-Token": token } });
    } finally {
      window.location.assign("/login.html");
    }
  }

  function renderIdentity(value) {
    identity = value || {};
    const displayName = identity.displayName || identity.userName || "Arbeitsplatz";
    const role = identity.roleLabel || identity.role || "Sitzung";
    const initial = displayName.trim().charAt(0).toUpperCase() || "?";
    document.querySelectorAll("#user-name, #popover-user").forEach(node => { node.textContent = displayName; });
    document.querySelectorAll("#user-role, #popover-role").forEach(node => { node.textContent = role; });
    const avatar = document.querySelector("#user-avatar");
    if (avatar) avatar.textContent = initial;
    const adminLink = document.querySelector("#user-administration-link");
    if (adminLink) adminLink.hidden = !hasPermission("users.manage");
    const logoutButton = document.querySelector("#logout-button");
    if (logoutButton) logoutButton.hidden = ["lan", "tailscale-proxy", "workstation-certificate"].includes(identity.accessMode);
    const passwordLink = document.querySelector("#change-password-link");
    if (passwordLink) passwordLink.hidden = identity.accessMode !== "session";
    document.documentElement.dataset.userRole = identity.role || "";
    document.dispatchEvent(new CustomEvent("novanein:identity", { detail: identity }));
    if (identity.mustChangePassword && !window.location.pathname.endsWith("/change-password.html")) {
      window.location.replace("/change-password.html");
    }
  }

  async function refreshCounts() {
    const [workItems, identityResult] = await Promise.allSettled([
      fetchJson("/api/v1/work-items/summary"),
      fetchJson("/auth/me")
    ]);
    if (workItems.status === "fulfilled") {
      const summary = workItems.value || {};
      renderCount("work-items", Math.max(0, Number(summary.total || 0) - Number(summary.completed || 0)));
    }
    if (identityResult.status === "fulfilled") {
      renderIdentity(identityResult.value);
      if (hasPermission("invoices.view")) {
        try {
          const proposals = await fetchJson("/api/v1/invoice-proposals");
          const items = Array.isArray(proposals) ? proposals : [];
          renderCount("invoice-proposals", items.filter(item => OPEN_PROPOSAL_STATUSES.has(item.status)).length);
        } catch {
          // A missing proposal count must not affect the rest of the navigation.
        }
      }
    }
  }

  function scheduleRefresh() {
    if (refreshTimer) window.clearInterval(refreshTimer);
    refreshTimer = window.setInterval(() => {
      if (!document.hidden) refreshCounts();
    }, REFRESH_INTERVAL_MS);
  }

  document.addEventListener("DOMContentLoaded", () => {
    ensureUserMenu();
    refreshCounts();
    scheduleRefresh();
  });
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) refreshCounts();
  });
  document.addEventListener("novanein:navigation-counts-refresh", refreshCounts);
}());
