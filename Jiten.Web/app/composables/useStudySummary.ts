import { useSrsStore } from '~/stores/srsStore';

/**
 * Shared read model over `srsStore.dueSummary`. The due count drives the header badge, the SRS
 * sub-nav and both study banners, and those must always agree, so the formula lives here only.
 */
export function useStudySummary() {
  const srsStore = useSrsStore();
  const router = useRouter();

  const loaded = computed(() => !!srsStore.dueSummary);

  // The review budget caps how many of the due reviews are actually offered today.
  const totalDue = computed(() => {
    const ds = srsStore.dueSummary;
    if (!ds) return 0;
    return Math.min(ds.reviewsDue, ds.reviewBudgetLeft) + ds.newCardsAvailable;
  });

  const goalReviewsDone = computed(() => srsStore.dueSummary?.reviewsToday ?? 0);
  const goalReviewsTarget = computed(() => {
    const ds = srsStore.dueSummary;
    if (!ds) return 0;
    return ds.reviewsToday + Math.min(ds.reviewsDue, ds.reviewBudgetLeft);
  });
  const goalNewDone = computed(() => srsStore.dueSummary?.newCardsToday ?? 0);
  const goalNewTarget = computed(() => srsStore.studySettings.newCardsPerDay);

  const nextReviewText = computed(() => {
    const ds = srsStore.dueSummary;
    if (!ds?.nextReviewAt) return null;
    const next = new Date(ds.nextReviewAt);
    const diffMs = next.getTime() - Date.now();
    if (diffMs <= 0) return 'now';
    const diffMin = Math.floor(diffMs / 60000);
    if (diffMin < 60) return `${diffMin}m`;
    const diffHr = Math.floor(diffMin / 60);
    if (diffHr < 24) return `${diffHr}h ${diffMin % 60}m`;
    return `${Math.floor(diffHr / 24)}d ${diffHr % 24}h`;
  });

  // Either source is authoritative when it says decks exist; only agreement on "none" hides the banner,
  // so a summary that lands before the deck list can't flash the "add your first deck" invitation.
  const hasStudyDecks = computed(() => srsStore.studyDecks.length > 0 || (srsStore.dueSummary?.hasStudyDecks ?? false));
  const isCaughtUp = computed(() => loaded.value && totalDue.value === 0);

  function startStudy() {
    srsStore.resetSession();
    router.push('/srs/study');
  }

  return {
    loaded,
    totalDue,
    goalReviewsDone,
    goalReviewsTarget,
    goalNewDone,
    goalNewTarget,
    nextReviewText,
    hasStudyDecks,
    isCaughtUp,
    startStudy,
  };
}
