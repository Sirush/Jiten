import { toRaw, type Ref } from 'vue';

const HISTORY_LIMIT = 200;

/** Snapshot undo and redo over a ref that only changes through `commit` or `reset`. */
export function useUndoHistory<T>(state: Ref<T>) {
  const past = shallowRef<T[]>([]);
  const future = shallowRef<T[]>([]);
  const snapshot = () => structuredClone(toRaw(state.value));

  /** Runs `change` on a copy; returning false drops the copy and leaves history untouched. */
  function commit(change: (draft: T) => unknown): boolean {
    const draft = snapshot();
    if (change(draft) === false) return false;
    past.value = [...past.value, snapshot()].slice(-HISTORY_LIMIT);
    future.value = [];
    state.value = draft;
    return true;
  }

  function step(from: Ref<T[]>, to: Ref<T[]>): boolean {
    const target = from.value.at(-1);
    if (target === undefined) return false;
    to.value = [...to.value, snapshot()];
    from.value = from.value.slice(0, -1);
    state.value = target;
    return true;
  }

  const undo = () => step(past, future);
  const redo = () => step(future, past);

  function reset(next: T) {
    state.value = next;
    past.value = [];
    future.value = [];
  }

  return {
    canUndo: computed(() => past.value.length > 0),
    canRedo: computed(() => future.value.length > 0),
    commit,
    undo,
    redo,
    reset,
  };
}
