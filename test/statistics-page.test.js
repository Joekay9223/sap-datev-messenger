import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../src/NovaNein.Server/", import.meta.url);
const web = new URL("wwwroot/", root);

test("main navigation replaces accounting reconciliation with statistics", async () => {
  for (const name of ["index.html", "booking.html", "history.html", "paperless.html", "admin-users.html"]) {
    const page = await readFile(new URL(name, web), "utf8");
    assert.match(page, /href="\/statistics\.html">Statistiken<\/a>/);
    assert.doesNotMatch(page, /href="\/accounting\.html">Buchhaltungsabgleich<\/a>/);
  }
});

test("statistics page offers upload and revenue views for all requested periods", async () => {
  const html = await readFile(new URL("statistics.html", web), "utf8");
  const script = await readFile(new URL("statistics.js", web), "utf8");
  const service = await readFile(new URL("BusinessStatisticsService.cs", root), "utf8");
  const program = await readFile(new URL("Program.cs", root), "utf8");

  assert.match(html, /data-view="uploads">Uploadstatistik/);
  assert.match(html, /data-view="revenue">Umsatzstatistik/);
  for (const key of ["today", "yesterday", "last7", "last30"]) assert.match(service, new RegExp(`new\\("${key}"`));
  assert.match(script, /\/api\/v1\/statistics\/overview/);
  assert.match(script, /Rechnungen abzüglich Gutschriften/);
  assert.match(program, /\/api\/v1\/statistics\/overview/);
  assert.match(program, /RequireAuthorization\("Accounting\.View"\)/);
});
