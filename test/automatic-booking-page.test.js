import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const html = readFileSync('src/NovaNein.Server/wwwroot/booking.html', 'utf8');
const js = readFileSync('src/NovaNein.Server/wwwroot/booking.js', 'utf8');
const directionStyles = readFileSync('src/NovaNein.Server/wwwroot/booking-direction.css', 'utf8');
const program = readFileSync('src/NovaNein.Server/Program.cs', 'utf8');
const config = JSON.parse(readFileSync('src/NovaNein.Server/appsettings.json', 'utf8'));

test('booking cockpit exposes review actions and PDF provenance', () => {
  assert.match(html, /Buchungsvorschläge/);
  assert.match(html, /Freigeben und buchen/);
  assert.match(html, /Originalrechnung/);
  assert.match(js, /expectedVersion/);
  assert.match(js, /line-account/);
  assert.match(js, /line-account-name/);
  assert.match(js, /\/api\/v1\/sap\/accounts\//);
  assert.match(js, /decision-reason/);
  assert.match(program, /\/api\/v1\/sap\/accounts\/\{account\}/);
});

test('booking writes use the CSRF header validated by the server', () => {
  assert.match(js, /'X-CSRF-Token':csrf/);
  assert.doesNotMatch(js, /X-NovaNein-CSRF/);
  assert.match(program, /Headers\["X-CSRF-Token"\]/);
  assert.match(html, /booking\.js\?v=20260717-direction1/);
});

test('approve and post shows a blocking progress state until SAP processing finishes', () => {
  const bookingStyles = readFileSync('src/NovaNein.Server/wwwroot/booking.css', 'utf8');
  assert.match(html, /id="posting-progress"[^>]*role="status"[^>]*aria-live="polite"/);
  assert.match(html, /SAP-Buchung läuft/);
  assert.match(html, /class="button-spinner"/);
  assert.match(html, /booking\.css\?v=20260716-posting-wait1/);
  assert.match(html, /booking\.js\?v=20260717-direction1/);
  assert.match(js, /function setProposalBusy\(busy\)/);
  assert.match(js, /Wird gebucht …/);
  assert.match(js, /if\(kind==='approve-and-post'\)\{setProposalBusy\(true\)/);
  assert.match(js, /finally\{if\(busy\)setProposalBusy\(false\)\}/);
  assert.match(js, /if\(proposalBusy\)event\.preventDefault\(\)/);
  assert.match(bookingStyles, /\.posting-progress-spinner,\.button-spinner\{[^}]*animation:posting-spin/);
});

test('successful posting opens the exact SAP document in the document cockpit', () => {
  assert.match(js, /current\.sapPosting\?\.docNum\|\|current\.invoiceNumber/);
  assert.match(js, /window\.location\.assign\(`\/index\.html\?search=\$\{encodeURIComponent\(cockpitSearch\)\}`\)/);
});

test('proposal overview renders a full-width accessible traffic-light list', () => {
  assert.match(html, /class="proposal-list-table"/);
  assert.match(html, /proposal-list-intro/);
  assert.match(html, /booking\.css\?v=20260716-posting-wait1/);
  assert.match(html, /booking\.js\?v=20260717-direction1/);
  assert.match(js, /`proposal-row proposal-row-\$\{tone\}`/);
  assert.match(js, /tabindex="0"/);
  assert.match(js, /proposal-state-pill/);
  assert.match(js, /event\.key==='Enter'\|\|event\.key===' '/);
  const bookingStyles = readFileSync('src/NovaNein.Server/wwwroot/booking.css', 'utf8');
  assert.match(bookingStyles, /\.proposal-row-green td\{[^}]*background:#eaf7f1/);
  assert.match(bookingStyles, /\.proposal-row-yellow td\{[^}]*background:#fff7dc/);
  assert.match(bookingStyles, /\.proposal-row-red td\{[^}]*background:#fff0ed/);
});

test('proposal overview separates open work from completed invoices', () => {
  assert.match(html, /data-view="open"[^>]*aria-pressed="true"/);
  assert.match(html, /data-view="completed"/);
  assert.match(html, /Neu eingegangen oder noch zu bearbeiten/);
  assert.match(html, /Gebucht, übertragen oder bewusst abgelehnt/);
  assert.match(js, /OPEN_PROPOSAL_STATUSES/);
  assert.match(js, /COMPLETED_PROPOSAL_STATUSES/);
  assert.match(js, /proposal-row-completed/);
  assert.match(js, /Keine offenen Buchungsvorschläge\$\{directionEmpty\}/);
  assert.match(js, /button\.setAttribute\('aria-pressed',String\(active\)\)/);
  const bookingStyles = readFileSync('src/NovaNein.Server/wwwroot/booking.css', 'utf8');
  assert.match(bookingStyles, /\.proposal-row-completed td\{[^}]*background:#f3f7f6/);
  assert.match(bookingStyles, /\.detail-status-banner\.completed/);
});

test('proposal overview separates incoming and outgoing invoices and displays the SAP document number', () => {
  assert.match(html, /data-direction="incoming"/);
  assert.match(html, /data-direction="outgoing"/);
  assert.match(html, /Eingangsrechnungen/);
  assert.match(html, /Ausgangsrechnungen/);
  assert.match(html, /booking-direction\.css\?v=20260717-direction1/);
  assert.match(js, /function directionMatches\(item\)/);
  assert.match(js, /currentDirection='all'/);
  assert.match(js, /SAP-Beleg \$\{esc\(sapDocumentNumber\)\}/);
  assert.match(js, /\['SAP-Belegnummer',current\.sapPosting\.docNum\]/);
  assert.match(directionStyles, /\.booking-direction-tabs button\[aria-pressed="true"\]/);
});

test('automatic booking endpoints and independent kill switches exist', () => {
  for (const endpoint of [
    '/api/v1/invoice-proposals',
    '/approve-and-post',
    '/api/v1/supplier-proposals',
    '/approve-and-create',
    '/api/v1/admin/gmail/status',
    '/api/v1/admin/gmail/sync'
  ]) assert.ok(program.includes(endpoint), endpoint);
  assert.equal(config.Gmail.Enabled, false);
  assert.equal(config.Sap.EnablePurchaseInvoiceWrites, false);
  assert.equal(config.Sap.EnableBusinessPartnerWrites, false);
  assert.equal(config.Datev.TransferAgentEnabled, false);
});

test('registered workstation certificates receive the existing reviewer permission as a formal role', () => {
  assert.match(program, /new Claim\(ClaimTypes\.Role, "Reviewer"\)/);
  assert.match(program, /new Claim\("novanein:access", "workstation-certificate"\)/);
  assert.match(program, /Admin and master-data roles remain user-login only/);
});

test('reviewers see a finished protected Gmail status instead of an endless loading state', () => {
  assert.match(js, /Gmail-Import geschützt/);
  assert.match(js, /Synchronisationsdetails sind nur mit Integrationsberechtigung sichtbar/);
  assert.match(js, /Gmail-Status nicht erreichbar/);
  assert.match(html, /booking\.js\?v=20260717-direction1/);
});

test('main cockpit links to booking proposals', () => {
  const index = readFileSync('src/NovaNein.Server/wwwroot/index.html', 'utf8');
  assert.match(index, /href="\/booking\.html"/);
});

test('top navigation uses readable buttons and live open-item badges', () => {
  const index = readFileSync('src/NovaNein.Server/wwwroot/index.html', 'utf8');
  const booking = readFileSync('src/NovaNein.Server/wwwroot/booking.html', 'utf8');
  const styles = readFileSync('src/NovaNein.Server/wwwroot/styles.css', 'utf8');
  const navigation = readFileSync('src/NovaNein.Server/wwwroot/navigation.js', 'utf8');

  for (const page of [index, booking]) {
    assert.match(page, /class="nav-button"/);
    assert.match(page, /data-nav-count="work-items"/);
    assert.match(page, /data-nav-count="invoice-proposals"/);
    assert.match(page, /navigation\.js\?v=20260716-users1/);
  }
  assert.match(index, /href="\/index\.html" aria-current="page"/);
  assert.match(booking, /href="\/booking\.html" aria-current="page"/);
  assert.match(styles, /\.nav-button \{[^}]*min-height: 40px;/);
  assert.match(styles, /\.nav-count \{[^}]*border-radius: 999px;/);
  assert.match(navigation, /\/api\/v1\/work-items\/summary/);
  assert.match(navigation, /\/auth\/me/);
  assert.match(navigation, /\/api\/v1\/invoice-proposals/);
  assert.match(navigation, /summary\.total/);
  assert.match(navigation, /summary\.completed/);
  assert.match(navigation, /OPEN_PROPOSAL_STATUSES/);
});
