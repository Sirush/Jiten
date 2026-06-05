// Accepted file extensions for SRS word-list imports, shared between the
// Add-Deck and Add-Words dialogs so they never drift apart.

// Files that can only be parsed as full text (vocabulary extracted from sentences),
// never as a one-word-per-line list.
export const FULL_TEXT_ONLY_EXTENSIONS = ['.epub', '.srt', '.ass', '.ssa', '.mokuro'];

// Word-list formats that may be parsed either per-line or as full text.
export const WORD_LIST_EXTENSIONS = ['.txt', '.csv', '.tsv'];

// All extensions accepted by the file pickers (value for an <input accept="...">).
export const IMPORT_ACCEPT_EXTENSIONS = [...WORD_LIST_EXTENSIONS, ...FULL_TEXT_ONLY_EXTENSIONS];

export const IMPORT_ACCEPT_ATTR = IMPORT_ACCEPT_EXTENSIONS.join(',');

// True when the given file name can only be parsed as full text.
export function isFullTextOnlyFile(fileName: string): boolean {
  const ext = fileName.replace(/^.*\./, '.').toLowerCase();
  return FULL_TEXT_ONLY_EXTENSIONS.includes(ext);
}
