// Version identifiers shown on the legal documents. The version accepted at purchase
// governs that purchase (CGV art. 5.3/12.4), so bump these only when the published
// text changes, and keep superseded versions reachable in the archive.
// Must be bumped in step with LegalDocumentsOptions (Jiten.Api), which the acceptance
// flow records; these values only label the published pages.
export const LEGAL_DOCUMENTS = {
  cgu: { version: '2026-08-08', effectiveDate: '8 August 2026', effectiveDateFr: '8 août 2026' },
  cgv: { version: '2026-08-08', effectiveDate: '8 August 2026', effectiveDateFr: '8 août 2026' },
} as const;
