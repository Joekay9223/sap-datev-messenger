(function () {
  "use strict";
  const form = document.querySelector("#login-form");
  const user = document.querySelector("#login-user");
  const password = document.querySelector("#login-password");
  const submit = document.querySelector("#login-submit");
  const feedback = document.querySelector("#login-feedback");
  let csrf = "";

  function show(message) {
    feedback.hidden = !message;
    feedback.textContent = message || "";
  }

  async function getCsrf() {
    const response = await fetch("/auth/csrf", { credentials: "include", headers: { Accept: "application/json", "X-Requested-With": "NovaNein-Web" } });
    if (!response.ok) throw new Error("Die Sicherheitsprüfung konnte nicht gestartet werden.");
    const payload = await response.json();
    csrf = payload.token || "";
  }

  async function checkSession() {
    try {
      const response = await fetch("/auth/me", { credentials: "include", headers: { Accept: "application/json" } });
      const contentType = response.headers.get("content-type") || "";
      if (response.ok && !response.redirected && contentType.includes("json")) {
        const payload = await response.json();
        if (payload && (payload.userName || payload.name)) window.location.replace(payload.mustChangePassword ? "/change-password.html" : "/");
      }
    } catch { /* Die Anmeldung zeigt die eigentliche Servermeldung. */ }
  }

  form.addEventListener("submit", async event => {
    event.preventDefault();
    show("");
    if (!user.value.trim() || !password.value) { show("Bitte Benutzername und Kennwort eingeben."); return; }
    submit.disabled = true; submit.textContent = "Anmeldung wird geprüft …";
    try {
      if (!csrf) await getCsrf();
      const response = await fetch("/auth/login", { method: "POST", credentials: "include", headers: { Accept: "application/json", "Content-Type": "application/json", "X-Requested-With": "NovaNein-Web", "X-CSRF-TOKEN": csrf }, body: JSON.stringify({ userName: user.value.trim(), password: password.value }) });
      let payload = null;
      try { payload = await response.json(); } catch { /* textlose Fehlerantwort */ }
      if (!response.ok) {
        if (response.status === 401) throw new Error("Benutzername oder Kennwort ist nicht korrekt.");
        if (response.status === 429) throw new Error("Zu viele Anmeldeversuche. Bitte warten Sie eine Minute.");
        throw new Error(payload && (payload.error || payload.detail) || "Die Anmeldung konnte nicht abgeschlossen werden.");
      }
      window.location.replace(payload?.mustChangePassword ? "/change-password.html" : "/");
    } catch (error) {
      show(error instanceof TypeError ? "Der NovaNein-Server ist nicht erreichbar." : error.message);
      submit.disabled = false; submit.textContent = "Sicher anmelden →";
    }
  });

  checkSession();
}());
