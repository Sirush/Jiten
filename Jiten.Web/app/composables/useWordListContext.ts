const STORAGE_KEY = 'jiten-word-list-context';
const MAX_REMEMBERED_ENTRIES = 8;

/** Identity of a rendered vocabulary row: [wordId, readingIndex]. */
export type WordListItem = [number, number];

export interface WordListContextInput {
  label: string;
  sortLabel?: string;
  sortDescending?: boolean;
  offset: number;
  totalItems: number;
  pageSize: number;
}

export interface WordListContext extends WordListContextInput {
  listPath: string;
  items: WordListItem[];
}

const ENTRY_STATE_KEY = 'jitenContextEntry';

interface StoredContexts {
  latest?: WordListContext;
  byEntry: Record<string, WordListContext>;
}

/**
 * Carries "which list did this word come from" from a vocabulary list to the word page.
 * sessionStorage rather than query params: the context must not leak into shared URLs and
 * must not participate in the word page's route key.
 */
export function useWordListContext() {
  const route = useRoute();

  /**
   * Identifies this exact history entry, minting an id into its state on first read.
   * Neither the router's `position` (reused whenever navigation branches) nor the path
   * (the same word is reachable from many lists) is unique enough on its own.
   */
  const entryId = (): string | null => {
    const state = (history.state ?? null) as Record<string, unknown> | null;
    const existing = state?.[ENTRY_STATE_KEY];
    if (typeof existing === 'string') return existing;

    const id = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
    try {
      history.replaceState({ ...state, [ENTRY_STATE_KEY]: id }, '');
    } catch {
      return null;
    }
    return id;
  };

  const readStore = (): StoredContexts => {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      const parsed = raw ? (JSON.parse(raw) as Partial<StoredContexts>) : null;
      return { latest: parsed?.latest, byEntry: parsed?.byEntry ?? {} };
    } catch {
      return { byEntry: {} };
    }
  };

  const writeStore = (store: StoredContexts) => {
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(store));
    } catch {
      // Private browsing or a full quota: the context bar is an enhancement, never a requirement.
    }
  };

  /** Entry ids are non-numeric, so key insertion order is claim order and the tail is the newest. */
  const pruned = (byEntry: Record<string, WordListContext>): Record<string, WordListContext> => {
    const kept = Object.keys(byEntry).slice(-MAX_REMEMBERED_ENTRIES);
    return Object.fromEntries(kept.map((id) => [id, byEntry[id]!]));
  };

  const isUsable = (context?: WordListContext): context is WordListContext =>
    !!context?.listPath && Array.isArray(context.items) && context.items.length > 0;

  const writeContext = (input: WordListContextInput, items: WordListItem[]) => {
    if (!import.meta.client) return;
    const store = readStore();
    store.latest = { ...input, listPath: route.fullPath, items };
    writeStore(store);
  };

  /**
   * A context claimed by this history entry wins over the most recent one, so going back to a
   * word page shows the list it was actually opened from rather than whichever list was
   * visited last. First read of an entry claims the current context for it.
   */
  const readContext = (): WordListContext | null => {
    if (!import.meta.client) return null;

    const store = readStore();
    const id = entryId();
    const claimed = id != null ? store.byEntry[id] : undefined;

    if (isUsable(claimed)) return claimed;
    if (!isUsable(store.latest)) return null;

    if (id != null) {
      store.byEntry[id] = store.latest;
      store.byEntry = pruned(store.byEntry);
      writeStore(store);
    }
    return store.latest;
  };

  return { writeContext, readContext };
}
