import path from 'node:path';

const INVOICE_XML = /^Eingangsrechnung-(\d+)\.xml$/;
const INVOICE_PDF = /^Eingangsrechnung-(\d+)\.pdf$/;

export function validateDatevPackage(entries) {
  const errors = [];
  if (!Array.isArray(entries)) return { valid: false, errors: ['Die ZIP-Einträge müssen als Liste vorliegen.'] };
  if (entries.length !== 3) errors.push('Ein DATEV-Buchungsdaten-ZIP muss exakt drei Dateien enthalten.');

  const names = entries.map((entry) => String(entry).replaceAll('\\', '/'));
  if (names.some((name) => name.includes('/') || name.startsWith('.'))) errors.push('ZIP-Dateien müssen im Stammverzeichnis liegen.');
  if (names.filter((name) => name === 'document.xml').length !== 1) errors.push('document.xml muss exakt einmal vorhanden sein.');

  const xml = names.map((name) => name.match(INVOICE_XML)).find(Boolean);
  const pdf = names.map((name) => name.match(INVOICE_PDF)).find(Boolean);
  if (!xml) errors.push('Es fehlt genau eine Rechnungs-XML im erwarteten Format.');
  if (!pdf) errors.push('Es fehlt genau eine Rechnungs-PDF im erwarteten Format.');
  if (xml && pdf && xml[1] !== pdf[1]) errors.push('Rechnungs-XML und Rechnungs-PDF müssen dieselbe SAP-Dokumentnummer haben.');

  return { valid: errors.length === 0, errors };
}

export function validateWatchfolderCandidate(candidate) {
  const extension = path.extname(String(candidate.fileName ?? '')).toLowerCase();
  const isWatchfolder = candidate.targetKind === 'datev-watchfolder';
  if (!isWatchfolder) return { valid: false, errors: ['Ziel ist kein registrierter DATEV-Watchfolder.'] };
  if (extension === '.pdf') return { valid: true, errors: [] };
  if (extension === '.zip') return validateDatevPackage(candidate.zipEntries);
  return { valid: false, errors: ['Im DATEV-Watchfolder sind nur PDFs oder geprüfte Paket-ZIPs zulässig.'] };
}

export function classifyTransferEvidence({ sapAttachmentPresent = false, archiveZipPresent = false, bttnextEvents = [] }) {
  const events = new Set(bttnextEvents);
  const transferred = archiveZipPresent && events.has('UploadSucceeded') && events.has('JobFinalized');
  return {
    sapAttachmentPresent,
    archiveZipPresent,
    transferred,
    status: transferred ? 'nachgewiesen' : 'nicht-nachgewiesen'
  };
}

export function deriveDatevBuCode({ avT1DatevCode }) {
  if (avT1DatevCode === undefined || avT1DatevCode === null || String(avT1DatevCode).trim() === '') {
    throw new Error('DATEV-BU-Code muss aus SAP AVT1.DatevCode stammen.');
  }
  return String(avT1DatevCode).trim();
}
