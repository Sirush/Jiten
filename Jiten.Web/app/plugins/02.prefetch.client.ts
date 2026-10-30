export default defineNuxtPlugin(() => {
  const authStore = useAuthStore();
  if (!authStore.isAuthenticated) return;

  const srs = useSrsStore();
  srs.fetchDueSummary();
  srs.fetchDeckStreak();

  const cached = srs.hydrateStudyDecksFromCache();
  if (!cached) {
    if (srs.studyDecks.length === 0) srs.fetchStudyDecks();
    return;
  }

  const revalidate = () => void srs.revalidateStudyDecks(cached);
  if ('requestIdleCallback' in window) window.requestIdleCallback(revalidate, { timeout: 4000 });
  else setTimeout(revalidate, 1500);
});
