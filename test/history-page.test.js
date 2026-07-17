import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../src/NovaNein.Server/wwwroot/", import.meta.url);

test("history page presents the last 50 document activities", async () => {
  const html = await readFile(new URL("history.html", root), "utf8");
  const script = await readFile(new URL("history.js", root), "utf8");
  assert.match(html, /Letzte 50 Aktionen/);
  assert.match(html, /id="history-body"/);
  assert.match(script, /fetch\("\/api\/v1\/activity\?limit=50"/);
  assert.match(script, /\/index\.html\?search=\$\{search\}/);
  assert.match(script, /ManualReviewApproved: \["Manuell freigegeben", "success"\]/);
});

test("history page keeps a single full-width activity table", async () => {
  const html = await readFile(new URL("history.html", root), "utf8");
  const styles = await readFile(new URL("history.css", root), "utf8");
  assert.match(html, /class="history-table"/);
  assert.match(styles, /\.history-table \{[^}]*width: 100%;/);
  assert.match(html, /href="\/index\.html"><span>Beleg-Cockpit<\/span><span class="nav-count"/);
});
