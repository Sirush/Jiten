import { useSrsStore } from '~/stores/srsStore';

// Wires up the triggers that persist / clear the in-progress study session cache. The store owns
// the serialization (persistSession / clearPersistedSession); this just decides *when* to fire.
// Call once from the study page setup.
export function useSrsSessionCache() {
  const srsStore = useSrsStore();

  function save() {
    srsStore.persistSession();
  }

  // Primary trigger: fires reliably when a mobile browser backgrounds the tab.
  function onVisibilityChange() {
    if (document.visibilityState === 'hidden') save();
  }

  // Crash backstop — debounced save as the working set changes (cursor moves / cards graded).
  let debounceTimer: ReturnType<typeof setTimeout> | null = null;
  const stopWatch = watch([() => srsStore.currentCardIndex, () => srsStore.currentBatch.length, () => srsStore.sessionStats.cardsReviewed], () => {
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(save, 1000);
  });

  // Once the session is done there is nothing to resume.
  const stopCompleteWatch = watch(
    () => srsStore.isSessionComplete,
    (done) => {
      if (done) srsStore.clearPersistedSession();
    }
  );

  onMounted(() => {
    document.addEventListener('visibilitychange', onVisibilityChange);
    window.addEventListener('pagehide', save);
  });

  onUnmounted(() => {
    document.removeEventListener('visibilitychange', onVisibilityChange);
    window.removeEventListener('pagehide', save);
    if (debounceTimer) clearTimeout(debounceTimer);
    stopWatch();
    stopCompleteWatch();
  });
}
