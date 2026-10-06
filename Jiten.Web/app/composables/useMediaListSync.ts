import { type Deck, type MediaListEntriesResponse, type MediaListEntrySummary, type MediaListSnapshot, DeckStatus } from '~/types';
import { useToast } from 'primevue/usetoast';
import { apiErrorMessage } from '~/utils/apiErrorMessage';
import { getDeckStatusText } from '~/utils/deckStatusMapper';
import { isoToday } from '~/utils/mediaListEntry';

/** Only the fields present changed: a status change carries status and listEntry, a favourite toggle only isFavourite. */
export interface MediaListChange {
  deckId: number;
  status?: DeckStatus;
  listEntry?: MediaListEntrySummary | null;
  isFavourite?: boolean;
}

export interface SetStatusOptions {
  /** yyyy-MM-dd in the viewer's calendar; null records the date as unknown, omitted means today. */
  date?: string | null;
  /** Start a new media list entry instead of reopening the current one. */
  newEntry?: boolean;
  /** Dropping a Completed title that was never actually finished: its completion becomes the dropped pass. */
  undoCompletion?: boolean;
  /** Offers to undo the change; on by default. */
  offerUndo?: boolean;
}

export type SeriesTitles = Pick<Deck, 'originalTitle' | 'romajiTitle' | 'englishTitle'>;

/** The deck a change is made on; a volume names its series so the series' summary can be refreshed. */
type ChangedDeck = { deckId: number; parentDeckId?: number | null };

export interface DeckStatusResponse {
  deckId: number;
  status: DeckStatus;
  isFavourite: boolean;
  isIgnored: boolean;
  listEntry: MediaListEntrySummary | null;
  parentDeckId: number | null;
  parentStatus: DeckStatus | null;
  allChildrenCompleted: boolean;
  /** Titles of the series, sent when all its volumes are completed. */
  parent: SeriesTitles | null;
  previous: MediaListSnapshot[];
}

interface RestoreResponse {
  decks: { deckId: number; status: DeckStatus; listEntry: MediaListEntrySummary | null; parentDeckId: number | null }[];
  parents: { deckId: number; status: DeckStatus }[];
}

/** What an Undo toast carries in its detail: the action its button runs. */
export interface UndoDetail {
  run: () => Promise<unknown>;
}

export const UNDO_TOAST_GROUP = 'media-list-undo';
const UNDO_VISIBLE_MS = 8000;

type Listener = (change: MediaListChange) => void;
const listeners = new Set<Listener>();

/** Tells every page showing a title that its status or entries changed, wherever the change was made. */
export function publishMediaListChange(change: MediaListChange) {
  for (const listener of listeners) listener(change);
}

/** Patches a page's own copies of a deck; the handler is dropped with the component that registered it. */
export function onMediaListChange(listener: Listener) {
  if (import.meta.server) return;
  listeners.add(listener);
  onScopeDispose(() => listeners.delete(listener));
}

const CHANGE_FIELDS = ['status', 'listEntry', 'isFavourite'] as const;

/** Applies a change to a deck when it is the one that changed; an unchanged deck comes back as the same object, so lists skip the re-render. */
export function applyMediaListChange<T extends Deck>(deck: T, change: MediaListChange): T {
  if (deck.deckId !== change.deckId) return deck;
  const fields = CHANGE_FIELDS.filter((field) => field in change && deck[field] !== change[field]);
  if (fields.length === 0) return deck;
  return { ...deck, ...Object.fromEntries(fields.map((field) => [field, change[field]])) };
}

/** Shallow equality: a card re-emitting a change its page already applied holds the same values. */
export function sameDeck<T extends Deck>(a: T, b: T): boolean {
  return (Object.keys(b) as (keyof T)[]).every((key) => a[key] === b[key]);
}

/** Swaps a deck into a list, returning the same list when the copy already there holds the same values. */
export function replaceDeck<T extends Deck>(list: T[], updated: T): T[] {
  const index = list.findIndex((d) => d.deckId === updated.deckId);
  const existing = list[index];
  if (!existing || sameDeck(existing, updated)) return list;
  const next = list.slice();
  next[index] = updated;
  return next;
}

/** The history dialog is mounted once in app.vue, so it outlives the popover, row or card that opened it. */
export const useHistoryDialogDeck = () => useState<Deck | null>('media-list-history-deck', () => null);

export function useMediaListApi() {
  const { $api } = useNuxtApp();
  const toast = useToast();

  /** A volume change moves its series' unit count and maybe its status; the series summary is refetched so its card is not left stale. */
  async function refreshSeries(seriesId: number) {
    try {
      const response = await $api<MediaListEntriesResponse>(`user/media-list/entries/${seriesId}`);
      publishMediaListChange({ deckId: seriesId, status: response.status, listEntry: response.summary });
    } catch {
      // The series card catches up on the next page load.
    }
  }

  /** Sets a status and tells every page about it, after the title's pending progress saves; on failure the user gets a toast and the result is null. */
  async function postDeckStatus(deck: ChangedDeck, status: DeckStatus, options: SetStatusOptions = {}): Promise<DeckStatusResponse | null> {
    const deckId = deck.deckId;
    let result: DeckStatusResponse | null = null;
    await enqueueDeckSave(
      deckId,
      async () => {
        try {
          const response = await $api<DeckStatusResponse>(`user/deck-preferences/${deckId}/status`, {
            method: 'POST',
            body: {
              status,
              date: options.date === undefined ? isoToday() : options.date,
              dateUnknown: options.date === null,
              newEntry: !!options.newEntry,
              undoCompletion: !!options.undoCompletion,
            },
          });
          publishMediaListChange({ deckId, status: response.status, listEntry: response.listEntry });
          const seriesId = response.parentDeckId ?? deck.parentDeckId;
          if (seriesId) void refreshSeries(seriesId);
          result = response;
          if (options.offerUndo !== false)
            offerUndo(status === DeckStatus.None ? 'Removed from your list' : `Moved to ${getDeckStatusText(status)}`, response.previous);
          return response.listEntry;
        } catch (e) {
          toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not update the status'), life: 5000 });
          return false;
        }
      },
      { quiet: true }
    );
    return result;
  }

  /** Publishes a reply from the entries endpoints, and refreshes the series a volume belongs to. */
  function publishEntries(response: MediaListEntriesResponse, deck: Pick<ChangedDeck, 'parentDeckId'>) {
    publishMediaListChange({ deckId: response.deckId, status: response.status, listEntry: response.summary });
    const seriesId = response.parentDeckId ?? deck.parentDeckId;
    if (seriesId) void refreshSeries(seriesId);
  }

  /** Puts titles back as a snapshot describes, in the first title's save queue so it lands after the saves still running there. */
  function restore(previous: MediaListSnapshot[]) {
    return enqueueDeckSave(previous[0]!.deckId, async () => {
      try {
        const response = await $api<RestoreResponse>('user/media-list/restore', { method: 'POST', body: { decks: previous } });
        for (const restored of response.decks) publishMediaListChange({ deckId: restored.deckId, status: restored.status, listEntry: restored.listEntry });
        const restoredIds = new Set(response.decks.map((d) => d.deckId));
        const seriesIds = new Set([...response.parents.map((p) => p.deckId), ...response.decks.map((d) => d.parentDeckId)]);
        for (const seriesId of seriesIds) if (seriesId != null && !restoredIds.has(seriesId)) void refreshSeries(seriesId);
        return response.decks.find((d) => d.deckId === previous[0]!.deckId)?.listEntry ?? null;
      } catch (e) {
        toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not undo the change'), life: 5000 });
        return false;
      }
    });
  }

  /** Only the latest change can be undone, so a new offer replaces the one showing. */
  function offerUndo(summary: string, previous: MediaListSnapshot[] | null | undefined) {
    if (!previous?.length) return;
    const detail: UndoDetail = { run: () => restore(previous) };
    toast.removeGroup(UNDO_TOAST_GROUP);
    toast.add({ group: UNDO_TOAST_GROUP, severity: 'secondary', summary, detail, life: UNDO_VISIBLE_MS });
  }

  return { postDeckStatus, refreshSeries, publishEntries, offerUndo };
}
