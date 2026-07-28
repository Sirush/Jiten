import type { UserHiddenDefinitionsDto } from '~/types';

// Per-tab singleton cache of the senses a user has hidden, keyed by word id. Hiding is word-level,
// so the same entry serves every reading of a word.
const hiddenByWord = ref(new Map<number, number[]>());
const requested = new Set<number>();
const queue = new Set<number>();
let flushScheduled = false;

// Only one word is editable at a time; the vocabulary page and the study card share this state so
// the toggle button and the definition list stay in sync without prop drilling.
const editingWordId = ref<number | null>(null);

const BATCH_LIMIT = 200;
const EMPTY: number[] = [];

export function useHiddenDefinitions() {
  const { $api } = useNuxtApp();
  const authStore = useAuthStore();

  function setEntry(wordId: number, indices: number[]) {
    const next = new Map(hiddenByWord.value);
    next.set(wordId, indices);
    hiddenByWord.value = next;
  }

  async function flush() {
    flushScheduled = false;
    const wordIds = [...queue];
    queue.clear();
    if (wordIds.length === 0) return;

    for (let i = 0; i < wordIds.length; i += BATCH_LIMIT) {
      const chunk = wordIds.slice(i, i + BATCH_LIMIT);
      try {
        const res = await $api<Record<string, number[]>>('user/hidden-definitions/batch', { method: 'POST', body: chunk });
        const next = new Map(hiddenByWord.value);
        for (const wordId of chunk) next.set(wordId, res[String(wordId)] ?? EMPTY);
        hiddenByWord.value = next;
      } catch {
        // Leave the words unmarked so a later mount retries; definitions render unfiltered meanwhile.
        chunk.forEach((wordId) => requested.delete(wordId));
      }
    }
  }

  function ensureLoaded(wordId: number | null | undefined) {
    if (!import.meta.client || wordId == null || !authStore.isAuthenticated) return;
    if (requested.has(wordId)) return;
    requested.add(wordId);
    queue.add(wordId);
    if (!flushScheduled) {
      flushScheduled = true;
      queueMicrotask(flush);
    }
  }

  function hiddenFor(wordId: number | null | undefined): number[] {
    if (wordId == null) return EMPTY;
    return hiddenByWord.value.get(wordId) ?? EMPTY;
  }

  async function setHidden(wordId: number, indices: number[]) {
    const sorted = [...new Set(indices)].sort((a, b) => a - b);
    const previous = hiddenFor(wordId);
    setEntry(wordId, sorted);
    try {
      const dto = await $api<UserHiddenDefinitionsDto>(`user/hidden-definitions/${wordId}`, {
        method: 'PUT',
        body: { hiddenIndices: sorted },
      });
      setEntry(wordId, dto.hiddenIndices ?? EMPTY);
    } catch (e) {
      setEntry(wordId, previous);
      throw e;
    }
  }

  function toggle(wordId: number, index: number) {
    const current = hiddenFor(wordId);
    return setHidden(wordId, current.includes(index) ? current.filter((i) => i !== index) : [...current, index]);
  }

  function isEditing(wordId: number | null | undefined) {
    return wordId != null && editingWordId.value === wordId;
  }

  function startEditing(wordId: number) {
    editingWordId.value = wordId;
  }

  function stopEditing(wordId?: number) {
    if (wordId == null || editingWordId.value === wordId) editingWordId.value = null;
  }

  return { hiddenByWord, hiddenFor, ensureLoaded, setHidden, toggle, editingWordId, isEditing, startEditing, stopEditing };
}
