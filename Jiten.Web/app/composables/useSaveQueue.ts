import type { MediaListEntrySummary } from '~/types';

export type SaveState = 'idle' | 'saving' | 'saved';

const SAVED_VISIBLE_MS = 2500;

/** Runs tasks one after another; a task resolving to false failed and already told the user, so "Saved" stays hidden. */
function createSaveQueue() {
  const state = ref<SaveState>('idle');
  let queue: Promise<unknown> = Promise.resolve();
  let pending = 0;
  let running = 0;
  let batchFailed = false;
  let savedTimer: ReturnType<typeof setTimeout> | undefined;

  /** A quiet task (a reload, a status change) waits its turn without showing "Saving…". */
  function enqueue(task: () => Promise<boolean>, { quiet = false } = {}): Promise<boolean> {
    running++;
    if (!quiet) {
      pending++;
      clearTimeout(savedTimer);
      state.value = 'saving';
    }

    const run = queue
      .then(task)
      .catch(() => false)
      .then((ok) => {
        running--;
        if (quiet) return ok;
        batchFailed ||= !ok;
        pending--;
        if (pending === 0) {
          state.value = batchFailed ? 'idle' : 'saved';
          batchFailed = false;
          if (state.value === 'saved') savedTimer = setTimeout(() => (state.value = 'idle'), SAVED_VISIBLE_MS);
        }
        return ok;
      });
    queue = run;
    return run;
  }

  const dispose = () => clearTimeout(savedTimer);
  const isIdle = () => running === 0;

  return { state, enqueue, dispose, isIdle };
}

type DeckQueue = ReturnType<typeof createSaveQueue> & { latest: MediaListEntrySummary | null; users: number };

const deckQueues = new Map<number, DeckQueue>();

function deckQueue(deckId: number): DeckQueue {
  let queue = deckQueues.get(deckId);
  if (!queue) {
    queue = { ...createSaveQueue(), latest: null, users: 0 };
    deckQueues.set(deckId, queue);
  }
  return queue;
}

function prune(deckId: number, queue: DeckQueue) {
  if (queue.users > 0 || !queue.isIdle() || deckQueues.get(deckId) !== queue) return;
  queue.dispose();
  deckQueues.delete(deckId);
}

/** Runs a save in the title's queue, so it lands in order with the progress step's and history dialog's saves. */
export function enqueueDeckSave(deckId: number, task: () => Promise<MediaListEntrySummary | null | false>, options: { quiet?: boolean } = {}) {
  const shared = deckQueue(deckId);
  return shared
    .enqueue(async () => {
      const result = await task();
      if (result === false) return false;
      shared.latest = result;
      return true;
    }, options)
    .finally(() => {
      if (!shared.isIdle()) return;
      shared.latest = null;
      prune(deckId, shared);
    });
}

/** One queue per title for the progress step and history dialog; `latest` is the last reply while saves are pending, before the deck props catch up. */
export function useDeckSaveQueue(deckId: number) {
  const shared = deckQueue(deckId);
  shared.users++;
  if (getCurrentScope())
    onScopeDispose(() => {
      shared.users--;
      prune(deckId, shared);
    });

  return {
    state: shared.state,
    enqueue: (task: () => Promise<MediaListEntrySummary | null | false>, options: { quiet?: boolean } = {}) => enqueueDeckSave(deckId, task, options),
    latest: () => shared.latest,
  };
}
