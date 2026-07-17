(function () {
  "use strict";
  const form = document.querySelector("#password-form");
  const current = document.querySelector("#current-password");
  const next = document.querySelector("#new-password");
  const confirm = document.querySelector("#confirm-password");
  const submit = document.querySelector("#password-submit");
  const feedback = document.querySelector("#password-feedback");
  let csrf = "";

  function show(message) {
    feedback.hidden = !message;
    feedback.textContent = message || "";
  }

  async function getCsrf() {
    const response = await fetch("/auth/csrf", { credentials: "include", headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error("Die Sicherheitsprüfung konnte nicht gestartet werden.");
    csrf = (await response.json()).token || "";
  }

  form.addEventListener("submit", async event => {
    event.preventDefault();
    show("");
    if (next.value !== confirm.value) { show("Die beiden neuen Kennwörter stimmen nicht überein."); return; }
    if (next.value.length < 14) { show("Das neue Kennwort muss mindestens 14 Zeichen lang sein."); return; }
    submit.disabled = true;
    submit.textContent = "Kennwort wird gespeichert …";
    try {
      if (!csrf) await getCsrf();
      const response = await fetch("/auth/change-password", {
        method: "POST",
        credentials: "include",
        headers: { Accept: "application/json", "Content-Type": "application/json", "X-CSRF-Token": csrf },
        body: JSON.stringify({ currentPassword: current.value, newPassword: next.value })
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(payload.error || "Das Kennwort konnte nicht geändert werden.");
      window.location.replace("/");
    } catch (error) {
      show(error.message);
      submit.disabled = false;
      submit.textContent = "Kennwort speichern →";
    }
  });
}());
