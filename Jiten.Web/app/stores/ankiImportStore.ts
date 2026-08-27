import { defineStore } from 'pinia';

export interface AnkiDeckImportSelection {
  deckName: string;
  fieldName: string;
  readingFieldName: string; // '' = none
  sentenceFieldName: string; // '' = none
  imageFieldName: string; // '' = none
  audioFieldName: string; // '' = none
  mediaConflictMode: 'skip' | 'replace' | 'ask';
  importReviewHistory: boolean;
  overwriteExisting: boolean;
  parseWords: boolean;
}

interface PersistedImportSettings {
  version: number;
  lastDeckId: number;
  decks: Record<string, AnkiDeckImportSelection>; // keyed by deck id
}

// Selection applied to a deck we haven't configured before (no field chosen, option defaults).
export function defaultImportSelection(): AnkiDeckImportSelection {
  return {
    deckName: '',
    fieldName: '',
    readingFieldName: '',
    sentenceFieldName: '',
    imageFieldName: '',
    audioFieldName: '',
    mediaConflictMode: 'skip',
    importReviewHistory: true,
    overwriteExisting: false,
    parseWords: false,
  };
}

export const useAnkiImportStore = defineStore('ankiImport', () => {
  const STORAGE_KEY = 'ankiconnect-import-settings';
  const VERSION = 2;

  const settings = ref<PersistedImportSettings>({ version: VERSION, lastDeckId: 0, decks: {} });

  function write() {
    if (!import.meta.client) return;
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(settings.value));
    } catch {}
  }

  function load() {
    if (!import.meta.client) return;
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const blob = JSON.parse(raw) as PersistedImportSettings;
      if (!blob?.decks) return;

      // v1 entries predate the sentence and media fields; they carry forward with those unset.
      if (blob.version === 1) {
        for (const deck of Object.values(blob.decks)) {
          deck.sentenceFieldName ??= '';
          deck.imageFieldName ??= '';
          deck.audioFieldName ??= '';
          deck.mediaConflictMode ??= 'skip';
        }
        blob.version = VERSION;
        settings.value = blob;
        write();
        return;
      }

      if (blob.version === VERSION) settings.value = blob;
    } catch {}
  }

  // Persist one deck's full selection, keyed by id, and remember it as the last-used deck.
  function saveDeckSelection(deckId: number, selection: AnkiDeckImportSelection) {
    if (!deckId) return;
    settings.value.lastDeckId = deckId;
    settings.value.decks[deckId] = selection;
    write();
  }

  // Resolve the deck to pre-select on connect: the last-used deck by id, then by its stored name as a
  // backup (Anki may have reassigned the id). Returns the matching deck id from `deckEntries`, or undefined.
  function findLastUsedDeckId(deckEntries: Array<[string, number]>): number | undefined {
    const lastId = settings.value.lastDeckId;
    if (!lastId) return undefined;
    const lastName = settings.value.decks[lastId]?.deckName;
    const match = deckEntries.find(([, id]) => id === lastId) ?? (lastName ? deckEntries.find(([name]) => name === lastName) : undefined);
    return match?.[1];
  }

  // Resolve a saved selection for the deck currently being loaded. Matches the EXACT same deck first
  // (id key AND stored name), then by name alone — migrating the entry to the deck's current id when Anki
  // reassigned it so old ids don't pile up. A plain id match alone is not trusted.
  function resolveDeckSelection(deckId: number, deckName: string | undefined): AnkiDeckImportSelection | undefined {
    const byId = settings.value.decks[deckId];
    if (byId && byId.deckName === deckName) return byId;
    if (!deckName) return undefined;
    const entry = Object.entries(settings.value.decks).find(([, d]) => d.deckName === deckName);
    if (!entry) return undefined;
    if (entry[0] !== String(deckId)) {
      const { [entry[0]]: _removed, ...rest } = settings.value.decks;
      settings.value.decks = rest;
      settings.value.decks[deckId] = entry[1];
      settings.value.lastDeckId = deckId;
      write();
    }
    return entry[1];
  }

  return { load, saveDeckSelection, findLastUsedDeckId, resolveDeckSelection };
});
