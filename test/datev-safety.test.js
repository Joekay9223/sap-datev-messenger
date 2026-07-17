import test from 'node:test';
import assert from 'node:assert/strict';
import {
  classifyTransferEvidence,
  deriveDatevBuCode,
  validateDatevPackage,
  validateWatchfolderCandidate
} from '../src/datev-safety.js';

const validEntries = ['document.xml', 'Eingangsrechnung-2614228.xml', 'Eingangsrechnung-2614228.pdf'];

test('akzeptiert ein gültiges DATEV-Drei-Dateien-ZIP', () => {
  assert.deepEqual(validateDatevPackage(validEntries), { valid: true, errors: [] });
});

test('weist ZIPs mit zusätzlichen oder fehlenden Dateien ab', () => {
  assert.equal(validateDatevPackage([...validEntries, 'README.txt']).valid, false);
  assert.equal(validateDatevPackage(validEntries.slice(0, 2)).valid, false);
});

test('blockiert lose XML-, CSV- und TXT-Dateien im DATEV-Watchfolder', () => {
  for (const fileName of ['document.xml', 'kontrolle.csv', 'hinweis.txt']) {
    assert.equal(validateWatchfolderCandidate({ fileName, targetKind: 'datev-watchfolder' }).valid, false);
  }
});

test('akzeptiert nur PDF oder geprüftes Paket-ZIP im DATEV-Watchfolder', () => {
  assert.equal(validateWatchfolderCandidate({ fileName: 'original.pdf', targetKind: 'datev-watchfolder' }).valid, true);
  assert.equal(validateWatchfolderCandidate({ fileName: 'paket.zip', zipEntries: validEntries, targetKind: 'datev-watchfolder' }).valid, true);
});

test('trennt SAP-Anhang von nachgewiesener DATEV-Übermittlung', () => {
  assert.equal(classifyTransferEvidence({ sapAttachmentPresent: true }).status, 'nicht-nachgewiesen');
  assert.equal(classifyTransferEvidence({ archiveZipPresent: true, bttnextEvents: ['UploadSucceeded', 'JobFinalized'] }).status, 'nachgewiesen');
});

test('leitet den DATEV-BU-Code aus SAP ab', () => {
  assert.equal(deriveDatevBuCode({ avT1DatevCode: 9 }), '9');
  assert.throws(() => deriveDatevBuCode({}), /AVT1.DatevCode/);
});
