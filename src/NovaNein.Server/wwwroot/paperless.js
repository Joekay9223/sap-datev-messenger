(() => {
  const rows = document.getElementById('documents');
  const empty = document.getElementById('empty');
  const feedback = document.getElementById('feedback');
  const state = document.getElementById('state');
  const query = document.getElementById('query');
  const csrf = () => document.querySelector('meta[name="csrf-token"]')?.content || '';
  async function loadCsrf() { const response = await fetch('/auth/csrf', { credentials: 'same-origin' }); if (response.ok) { const data = await response.json(); document.querySelector('meta[name="csrf-token"]').content = data.token; } }
  function showError(message) { feedback.textContent = message; feedback.hidden = false; state.textContent = 'Prüfung erforderlich'; }
  function render(items) {
    rows.replaceChildren(); empty.hidden = items.length !== 0;
    for (const item of items) {
      const tr = document.createElement('tr');
      const values = [item.id, item.title || '—', item.correspondent || '—', item.documentDate || '—'];
      for (const value of values) { const cell = document.createElement('td'); cell.textContent = value; tr.append(cell); }
      const action = document.createElement('td'); const link = document.createElement('a'); link.className = 'button button-outline button-small'; link.href = `/api/v1/paperless/documents/${encodeURIComponent(item.id)}/file`; link.textContent = 'PDF öffnen'; link.target = '_blank'; link.rel = 'noopener'; action.append(link); tr.append(action); rows.append(tr);
    }
  }
  async function load() {
    feedback.hidden = true; state.textContent = 'Paperless wird gelesen …';
    const parameter = query.value.trim() ? `?query=${encodeURIComponent(query.value.trim())}` : '';
    try { const response = await fetch(`/api/v1/paperless/documents${parameter}`, { credentials: 'same-origin' }); const data = await response.json(); if (!response.ok) throw new Error(data.error || data.detail || 'Paperless ist nicht erreichbar.'); render(data.results || []); state.textContent = `${data.count || 0} Dokumente`; }
    catch (error) { render([]); showError(error.message); }
  }
  document.getElementById('reload').addEventListener('click', load); query.addEventListener('change', load); document.getElementById('logout').addEventListener('click', async () => { await fetch('/auth/logout', { method: 'POST', headers: { 'X-NovaNein-CSRF': csrf() }, credentials: 'same-origin' }); location.href = '/login.html'; });
  loadCsrf().then(load);
})();
