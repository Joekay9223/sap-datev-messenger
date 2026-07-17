(function () {
  "use strict";
  const $ = id => document.getElementById(id);
  let csrf = "";
  let users = [];
  let catalog = [];
  let roles = [];

  function escapeHtml(value) { return String(value ?? "").replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[character])); }
  function formatDate(value) { if (!value) return "Noch nie"; const date = new Date(value); return Number.isNaN(date.getTime()) ? "—" : new Intl.DateTimeFormat("de-DE", { dateStyle: "short", timeStyle: "short" }).format(date); }
  function show(message, target = $("users-feedback")) { target.hidden = !message; target.textContent = message || ""; }

  async function api(path, options = {}) {
    const write = options.method && options.method !== "GET";
    if (write && !csrf) {
      const response = await fetch("/auth/csrf", { credentials: "include", headers: { Accept: "application/json" } });
      csrf = (await response.json()).token || "";
    }
    const response = await fetch(path, {
      credentials: "include",
      ...options,
      headers: { Accept: "application/json", ...(options.body ? { "Content-Type": "application/json" } : {}), ...(write ? { "X-CSRF-Token": csrf } : {}), ...(options.headers || {}) }
    });
    const payload = response.status === 204 ? null : await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(payload?.error || `Serverantwort ${response.status}`);
    return payload;
  }

  function roleInfo(role) { return roles.find(item => item.key === role) || { label: role, defaultPermissions: [] }; }
  function permissionLabels(keys) { return keys.map(key => catalog.find(item => item.key === key)?.label || key); }
  function status(user) {
    if (!user.isActive) return ["Deaktiviert", "disabled"];
    if (user.lockedUntil && new Date(user.lockedUntil) > new Date()) return ["Gesperrt", "locked"];
    if (user.mustChangePassword) return ["Erstzugang offen", "pending"];
    return ["Aktiv", "active"];
  }

  function renderUsers() {
    $("metric-users").textContent = users.length;
    $("metric-active").textContent = users.filter(user => user.isActive).length;
    $("metric-password").textContent = users.filter(user => user.mustChangePassword).length;
    $("metric-locked").textContent = users.filter(user => user.lockedUntil && new Date(user.lockedUntil) > new Date()).length;
    $("users-empty").hidden = users.length > 0;
    $("users-body").innerHTML = users.map(user => {
      const [statusLabel, statusState] = status(user);
      const labels = permissionLabels(user.permissions || []);
      return `<tr>
        <td><span class="user-identity"><strong>${escapeHtml(user.displayName)}</strong><span>${escapeHtml(user.userName)} · ${escapeHtml(user.email)}</span></span></td>
        <td><span class="role-pill">${escapeHtml(roleInfo(user.role).label)}</span></td>
        <td><span class="permission-summary">${escapeHtml(labels.slice(0, 3).join(", "))}${labels.length > 3 ? ` und ${labels.length - 3} weitere` : ""}</span></td>
        <td>${escapeHtml(formatDate(user.lastLoginAt))}</td>
        <td><span class="account-status" data-state="${statusState}">${statusLabel}</span></td>
        <td><div class="user-actions"><button class="row-action secondary" data-action="edit" data-id="${user.id}">Bearbeiten</button><button class="row-action" data-action="reset" data-id="${user.id}">Kennwort zurücksetzen</button></div></td>
      </tr>`;
    }).join("");
    $("users-updated").textContent = `Aktualisiert: ${formatDate(new Date().toISOString())}`;
  }

  function renderPermissionGrid(selected = []) {
    const selectedSet = new Set(selected);
    $("permission-grid").innerHTML = catalog.map(permission => `<label class="permission-option"><input type="checkbox" value="${escapeHtml(permission.key)}" ${selectedSet.has(permission.key) ? "checked" : ""}><span><strong>${escapeHtml(permission.label)}</strong><span>${escapeHtml(permission.description)}</span></span></label>`).join("");
  }

  function selectedPermissions() { return Array.from($("permission-grid").querySelectorAll("input:checked"), input => input.value); }
  function useRoleDefaults() { renderPermissionGrid(roleInfo($("user-role-input").value).defaultPermissions || []); }

  function openUser(user = null) {
    $("user-form").reset();
    $("user-id").value = user?.id || "";
    $("user-dialog-title").textContent = user ? "Benutzer bearbeiten" : "Benutzer anlegen";
    $("user-display-name").value = user?.displayName || "";
    $("user-name-input").value = user?.userName || "";
    $("user-name-input").disabled = Boolean(user);
    $("user-email").value = user?.email || "";
    $("user-role-input").value = user?.role || "Operator";
    $("user-active").checked = user?.isActive ?? true;
    renderPermissionGrid(user?.permissions || roleInfo("Operator").defaultPermissions);
    show("", $("user-form-feedback"));
    $("user-dialog").showModal();
  }

  function showCredentials(result) {
    $("credential-user").textContent = result.user.userName;
    $("credential-password").textContent = result.temporaryPassword || "Nicht neu erzeugt";
    $("credential-dialog").showModal();
  }

  async function saveUser(event) {
    event.preventDefault();
    show("", $("user-form-feedback"));
    const id = $("user-id").value;
    const body = {
      userName: $("user-name-input").value.trim(),
      displayName: $("user-display-name").value.trim(),
      email: $("user-email").value.trim(),
      role: $("user-role-input").value,
      permissions: selectedPermissions(),
      isActive: $("user-active").checked,
      mustChangePassword: true
    };
    $("save-user").disabled = true;
    try {
      const result = id
        ? await api(`/api/v1/admin/users/${id}`, { method: "PUT", body: JSON.stringify(body) })
        : await api("/api/v1/admin/users", { method: "POST", body: JSON.stringify(body) });
      $("user-dialog").close();
      await loadUsers();
      if (!id) showCredentials(result);
    } catch (error) {
      show(error.message, $("user-form-feedback"));
    } finally { $("save-user").disabled = false; }
  }

  async function resetPassword(id) {
    const user = users.find(item => item.id === id);
    if (!user || !window.confirm(`Temporäres Kennwort für ${user.displayName} neu erzeugen?`)) return;
    try {
      const result = await api(`/api/v1/admin/users/${id}/reset-password`, { method: "POST", body: JSON.stringify({ mustChangePassword: true }) });
      showCredentials(result);
      await Promise.all([loadUsers(), loadAudit()]);
    } catch (error) { show(error.message); }
  }

  async function loadUsers() {
    users = await api("/api/v1/admin/users");
    renderUsers();
  }

  async function loadAudit() {
    const entries = await api("/api/v1/admin/user-audit?limit=100");
    $("audit-body").innerHTML = entries.map(entry => `<tr><td>${escapeHtml(formatDate(entry.occurredAt))}</td><td>${escapeHtml(entry.userName)}</td><td>${escapeHtml(entry.action)}</td><td>${escapeHtml(entry.detail)}</td><td>${escapeHtml(entry.remoteAddress || "Intern")}</td></tr>`).join("");
  }

  async function initialize() {
    try {
      const definitions = await api("/api/v1/admin/permissions");
      catalog = definitions.permissions || [];
      roles = definitions.roles || [];
      $("user-role-input").innerHTML = roles.map(role => `<option value="${escapeHtml(role.key)}">${escapeHtml(role.label)}</option>`).join("");
      await Promise.all([loadUsers(), loadAudit()]);
    } catch (error) { show(error.message); }
  }

  $("create-user").addEventListener("click", () => openUser());
  $("user-role-input").addEventListener("change", useRoleDefaults);
  $("reset-role-permissions").addEventListener("click", useRoleDefaults);
  $("user-form").addEventListener("submit", saveUser);
  $("users-body").addEventListener("click", event => {
    const button = event.target.closest("button[data-action]");
    if (!button) return;
    if (button.dataset.action === "edit") openUser(users.find(user => user.id === button.dataset.id));
    if (button.dataset.action === "reset") resetPassword(button.dataset.id);
  });
  $("refresh-audit").addEventListener("click", loadAudit);
  $("close-credentials").addEventListener("click", () => $("credential-dialog").close());
  $("copy-credentials").addEventListener("click", async () => {
    await navigator.clipboard.writeText(`Benutzername: ${$("credential-user").textContent}\nTemporäres Kennwort: ${$("credential-password").textContent}`);
    $("copy-credentials").textContent = "Kopiert";
  });
  initialize();
}());
