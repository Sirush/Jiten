import { defineStore } from 'pinia';
import type { StudyDeckDto, StudyBatchResponse, StudyCardDto, StudySettingsDto, AddStudyDeckRequest, UpdateStudyDeckRequest, DueSummaryDto, DeckStreakDto, ReviewForecast30dDto, StudyMoreParams, CardExamplesResponse, StudyExampleSentenceDto, SessionStreakDto, ReviewForecastDto } from '~/types';
import { FsrsRating } from '~/types';
import { DEFAULT_KEYBINDS } from '~/composables/useStudyKeyboard';

interface SessionReview {
  wordId: number;
  readingIndex: number;
  wordText: string;
  reading: string;
  rating: FsrsRating;
  duration: number | undefined;
}

export interface LeechCard {
  wordId: number;
  readingIndex: number;
  wordText: string;
  reading: string;
  suspended: boolean;
}

export interface HardestCard {
  wordId: number;
  readingIndex: number;
  wordText: string;
  reading: string;
  againCount: number;
  reviewCount: number;
  avgDuration: number;
}

interface PendingReview {
  cardKey: string;
  card: StudyCardDto;
  rating: FsrsRating;
  reviewEntry: SessionReview;
  reinsertedAgainCard: StudyCardDto | null;
  epoch: number;
  deltas: {
    counted: boolean;
    wasNew: boolean;
    correct: boolean;
    gradeKey: 'again' | 'hard' | 'good' | 'easy';
    clearedGrade: 'hard' | 'good' | 'easy' | null;
  };
}

interface UndoSnapshot {
  card: StudyCardDto;
  type: 'grade' | 'blacklist' | 'master' | 'forget' | 'suspend' | 'bury';
  rating?: FsrsRating;
  batch: StudyCardDto[];
  cardIndex: number;
  againKeys: Set<string>;
  grades: ('hard' | 'good' | 'easy' | 'action')[];
  reviews: SessionReview[];
  leeches: LeechCard[];
  stats: {
    cardsReviewed: number;
    newCardsLearned: number;
    correctCount: number;
    startTime: Date | null;
    gradeCounts: { again: number; hard: number; good: number; easy: number };
  };
}

export const useSrsStore = defineStore('srs', () => {
  const { $api } = useNuxtApp();

  const srsEnrolled = ref<boolean | null>(null);
  const studyDecks = ref<StudyDeckDto[]>([]);
  const overviewVersion = ref<number>(0);
  const sessionId = ref<string | null>(null);
  const currentBatch = ref<StudyCardDto[]>([]);
  const currentCardIndex = ref(0);
  const isFlipped = ref(false);
  const isLoading = ref(false);
  const isSessionComplete = ref(false);
  const isWrappingUp = ref(false);
  const preWrapUpBatch = ref<StudyCardDto[]>([]);
  const settingsLoaded = ref(false);
  const studySettings = ref<StudySettingsDto>({
    newCardsPerDay: 20,
    maxReviewsPerDay: 200,
    batchSize: 100,
    gradingButtons: 4,
    interleaving: 'Mixed',
    newCardGathering: 'TopDeck',
    reviewFrom: 'AllTracked',
    showPitchAccent: true,
    exampleSentencePosition: 'Back',
    exampleSentenceSorting: 'Random',
    blurExampleSentence: false,
    showFrequencyRank: true,
    showKanjiBreakdown: true,
    showWordComposition: true,
    showWordUsedIn: true,
    showNextInterval: true,
    showKeybinds: true,
    showElapsedTime: true,
    enableSwipeGesture: true,
    countFailedReviews: true,
    showFuriganaOnFront: false,
    furiganaOnFrontNewOnly: false,
    autoPlayWord: true,
    autoPlaySentence: true,
    autoPlayWordOnFront: false,
    autoPlayWordOnFrontNewOnly: false,
    autoPlaySentenceOnFront: false,
    showReviewActivity: true,
    showReviewForecast: true,
    timezone: 'Europe/London',
    showConfusableReadings: true,
    dayBoundaryScheduling: false,
    leechThreshold: 8,
    leechAction: 'Suspend',
    keybinds: { ...DEFAULT_KEYBINDS },
  });
  const lastLeechEvent = ref<{ detected: boolean; suspended: boolean } | null>(null);
  const sessionStats = ref({
    cardsReviewed: 0,
    newCardsLearned: 0,
    correctCount: 0,
    startTime: null as Date | null,
    gradeCounts: { again: 0, hard: 0, good: 0, easy: 0 },
  });
  const newCardsRemaining = ref(0);
  const reviewsRemaining = ref(0);
  const newCardsToday = ref(0);
  const reviewsToday = ref(0);
  const againCardKeys = ref(new Set<string>());
  const clearedGrades = ref<('hard' | 'good' | 'easy' | 'action')[]>([]);

  const MAX_UNDO_DEPTH = 100;
  const undoStack = ref<UndoSnapshot[]>([]);
  const sessionReviews = ref<SessionReview[]>([]);
  const sessionLeeches = ref<LeechCard[]>([]);
  const cardShownAt = ref<number | null>(null);
  const thinkingDuration = ref<number | undefined>(undefined);
  const isBusy = ref(false);
  const fetchError = ref<string | null>(null);
  // Surfaced to the study page so it can toast when an optimistic review failed to persist.
  const lastReviewError = ref<{ wordText: string } | null>(null);
  // In-flight background review requests, keyed by card. Lets undo wait for the right one.
  const inFlightReviews = new Map<string, Promise<void>>();
  // Bumped whenever the session is reset, so late background results from an old session are ignored.
  let sessionEpoch = 0;
  const dueSummary = ref<DueSummaryDto | null>(null);
  const deckStreak = ref<DeckStreakDto | null>(null);
  const reviewForecast30d = ref<ReviewForecast30dDto | null>(null);
  const studyMoreParams = ref<StudyMoreParams | null>(null);
  const exampleCache = ref(new Map<string, StudyExampleSentenceDto | null>());
  const examplePrefetchedUpTo = ref(-1);
  const sessionDirty = ref(false);
  const sessionStreak = ref<SessionStreakDto | null>(null);
  const sessionForecast = ref<ReviewForecastDto | null>(null);
  let summaryPrefetched = false;

  function prefetchSessionSummary() {
    if (summaryPrefetched) return;
    summaryPrefetched = true;
    $api<SessionStreakDto>('srs/session-streak').then(r => { sessionStreak.value = r; }).catch(() => {});
    $api<ReviewForecastDto>('srs/review-forecast').then(r => { sessionForecast.value = r; }).catch(() => {});
  }

  function invalidateSession() {
    sessionDirty.value = true;
  }

  const canUndo = computed(() => undoStack.value.length > 0);

  const currentCard = computed(() =>
    currentCardIndex.value < currentBatch.value.length
      ? currentBatch.value[currentCardIndex.value]
      : null
  );

  const hasCards = computed(() => currentBatch.value.length > 0 && currentCardIndex.value < currentBatch.value.length);

  const progress = computed(() => ({
    current: Math.min(currentCardIndex.value + 1, currentBatch.value.length),
    total: currentBatch.value.length,
    reviewed: sessionStats.value.cardsReviewed,
    remaining: currentBatch.value.length - currentCardIndex.value,
  }));

  const remainingByType = computed(() => {
    let newCount = 0;
    let reviewCount = 0;
    for (let i = currentCardIndex.value; i < currentBatch.value.length; i++) {
      if (currentBatch.value[i].isNewCard) newCount++;
      else reviewCount++;
    }
    return { new: newCount, review: reviewCount };
  });

  const againCardsAhead = computed(() => {
    let count = 0;
    for (let i = currentCardIndex.value; i < currentBatch.value.length; i++) {
      const c = currentBatch.value[i];
      if (againCardKeys.value.has(`${c.wordId}-${c.readingIndex}`)) count++;
    }
    return count;
  });

  const hardestCards = computed<HardestCard[]>(() => {
    const grouped = new Map<string, HardestCard>();
    for (const r of sessionReviews.value) {
      const key = `${r.wordId}-${r.readingIndex}`;
      const existing = grouped.get(key);
      if (existing) {
        if (r.rating === FsrsRating.Again) existing.againCount++;
        if (r.duration) existing.avgDuration += r.duration;
        existing.reviewCount++;
      } else {
        grouped.set(key, {
          wordId: r.wordId,
          readingIndex: r.readingIndex,
          wordText: r.wordText,
          reading: r.reading,
          againCount: r.rating === FsrsRating.Again ? 1 : 0,
          reviewCount: 1,
          avgDuration: r.duration ?? 0,
        });
      }
    }
    for (const card of grouped.values()) {
      if (card.reviewCount > 0) card.avgDuration = Math.round(card.avgDuration / card.reviewCount);
    }
    return [...grouped.values()]
      .filter(c => c.againCount > 0)
      .sort((a, b) => b.againCount - a.againCount || b.avgDuration - a.avgDuration)
      .slice(0, 5);
  });

  function takeSnapshot(card: StudyCardDto, type: UndoSnapshot['type'], rating?: FsrsRating) {
    const snapshot: UndoSnapshot = {
      card,
      type,
      rating,
      batch: [...currentBatch.value],
      cardIndex: currentCardIndex.value,
      againKeys: new Set(againCardKeys.value),
      grades: [...clearedGrades.value],
      reviews: [...sessionReviews.value],
      leeches: [...sessionLeeches.value],
      stats: JSON.parse(JSON.stringify(sessionStats.value)),
    };
    const stack = [...undoStack.value, snapshot];
    if (stack.length > MAX_UNDO_DEPTH) stack.shift();
    undoStack.value = stack;
  }

  let fetchStudyDecksPromise: Promise<void> | null = null;

  async function fetchStudyDecks() {
    if (fetchStudyDecksPromise) return fetchStudyDecksPromise;
    fetchStudyDecksPromise = (async () => {
      try {
        studyDecks.value = await $api<StudyDeckDto[]>('srs/study-decks');
        fetchError.value = null;
      } catch (error) {
        console.error('Failed to fetch study decks:', error);
        fetchError.value = 'Failed to load study decks. Please try again.';
      } finally {
        fetchStudyDecksPromise = null;
      }
    })();
    return fetchStudyDecksPromise;
  }

  let fetchDueSummaryPromise: Promise<void> | null = null;

  async function fetchDueSummary() {
    if (fetchDueSummaryPromise) return fetchDueSummaryPromise;
    fetchDueSummaryPromise = (async () => {
      try {
        dueSummary.value = await $api<DueSummaryDto>('srs/due-summary');
      } catch {
        dueSummary.value = null;
      } finally {
        fetchDueSummaryPromise = null;
      }
    })();
    return fetchDueSummaryPromise;
  }

  let fetchDeckStreakPromise: Promise<void> | null = null;

  async function fetchDeckStreak() {
    if (fetchDeckStreakPromise) return fetchDeckStreakPromise;
    fetchDeckStreakPromise = (async () => {
      try {
        deckStreak.value = await $api<DeckStreakDto>('srs/deck-streak');
      } catch {
        deckStreak.value = null;
      } finally {
        fetchDeckStreakPromise = null;
      }
    })();
    return fetchDeckStreakPromise;
  }

  async function fetchReviewForecast30d() {
    try {
      reviewForecast30d.value = await $api<ReviewForecast30dDto>('srs/review-forecast-30d');
    } catch {
      reviewForecast30d.value = null;
    }
  }

  let refreshOverviewPromise: Promise<void> | null = null;

  async function refreshOverview(force = false) {
    if (refreshOverviewPromise) return refreshOverviewPromise;
    refreshOverviewPromise = (async () => {
      try {
        let serverVersion = 0;
        try {
          const { version } = await $api<{ version: number }>('srs/overview-version');
          serverVersion = version;
          if (!force && overviewVersion.value > 0 && version === overviewVersion.value) return;
        } catch { /* fall through to full refresh */ }
        await Promise.all([fetchStudyDecks(), fetchDueSummary(), fetchDeckStreak(), fetchSettings(true)]);
        overviewVersion.value = serverVersion;
      } finally {
        refreshOverviewPromise = null;
      }
    })();
    return refreshOverviewPromise;
  }

  async function addStudyDeck(request: AddStudyDeckRequest) {
    const result = await $api<{ userStudyDeckId: number }>('srs/study-decks', {
      method: 'POST',
      body: request,
    });
    studyDecks.value.push({
      userStudyDeckId: result.userStudyDeckId,
      deckType: request.deckType,
      name: request.name ?? '',
      deckId: request.deckId,
      title: '',
      mediaType: 0 as any,
      sortOrder: studyDecks.value.length,
      isActive: true,
      downloadType: request.downloadType,
      order: request.order,
      minFrequency: request.minFrequency,
      maxFrequency: request.maxFrequency,
      targetPercentage: request.targetPercentage,
      minOccurrences: request.minOccurrences,
      maxOccurrences: request.maxOccurrences,
      excludeKana: request.excludeKana,
      minGlobalFrequency: request.minGlobalFrequency,
      maxGlobalFrequency: request.maxGlobalFrequency,
      posFilter: request.posFilter,
      totalWords: 0,
      unseenCount: 0,
      learningCount: 0,
      reviewCount: 0,
      masteredCount: 0,
      blacklistedCount: 0,
      suspendedCount: 0,
      dueReviewCount: 0,
    });
    refreshOverview();
    invalidateSession();
    return result;
  }

  async function updateStudyDeck(id: number, request: UpdateStudyDeckRequest) {
    await $api(`srs/study-decks/${id}`, {
      method: 'PUT',
      body: request,
    });
    const deck = studyDecks.value.find(d => d.userStudyDeckId === id);
    if (deck) {
      if (request.name != null) deck.name = request.name;
      if (request.description != null) deck.description = request.description;
      deck.downloadType = request.downloadType;
      deck.order = request.order;
      deck.minFrequency = request.minFrequency;
      deck.maxFrequency = request.maxFrequency;
      deck.targetPercentage = request.targetPercentage;
      deck.minOccurrences = request.minOccurrences;
      deck.maxOccurrences = request.maxOccurrences;
      deck.excludeKana = request.excludeKana;
      deck.minGlobalFrequency = request.minGlobalFrequency;
      deck.maxGlobalFrequency = request.maxGlobalFrequency;
      deck.posFilter = request.posFilter;
    }
    refreshOverview();
    invalidateSession();
  }

  async function removeStudyDeck(id: number) {
    await $api(`srs/study-decks/${id}`, { method: 'DELETE' });
    studyDecks.value = studyDecks.value.filter(d => d.userStudyDeckId !== id);
    invalidateSession();
    refreshOverview();
  }

  async function addDeckWord(deckId: number, wordId: number, readingIndex: number, occurrences = 1) {
    await $api(`srs/study-decks/${deckId}/words`, {
      method: 'POST',
      body: { wordId, readingIndex, occurrences },
    });
    refreshOverview();
  }

  async function removeDeckWord(deckId: number, wordId: number, readingIndex: number) {
    await $api(`srs/study-decks/${deckId}/words/${wordId}/${readingIndex}`, { method: 'DELETE' });
    refreshOverview();
  }

  async function updateDeckWordOccurrences(deckId: number, wordId: number, readingIndex: number, occurrences: number) {
    await $api(`srs/study-decks/${deckId}/words/${wordId}/${readingIndex}`, {
      method: 'PATCH',
      body: { occurrences },
    });
    refreshOverview();
  }

  async function importPreview(file: File, parseFullText = false) {
    if (file.name.endsWith('.epub')) {
      const { stripEpubImages } = await import('~/utils/epubStripper');
      file = await stripEpubImages(file);
    }
    const formData = new FormData();
    formData.append('file', file);
    if (parseFullText) formData.append('parseFullText', 'true');
    return await $api<{ matched: any[]; unmatched: string[]; totalLines: number; previewToken: string }>('srs/study-decks/import/preview', {
      method: 'POST',
      body: formData,
    });
  }

  async function importCommit(previewToken: string, name: string, description?: string, excludeWordIds?: number[]) {
    const result = await $api<{ userStudyDeckId: number }>('srs/study-decks/import', {
      method: 'POST',
      body: { previewToken, name, description, excludeWordIds },
    });
    refreshOverview();
    invalidateSession();
    return result;
  }

  async function importPreviewText(lines: string[], parseFullText = false) {
    return await $api<{ matched: any[]; unmatched: string[]; totalLines: number; previewToken: string }>('srs/study-decks/import/preview-text', {
      method: 'POST',
      body: { lines, parseFullText },
    });
  }

  async function importToExistingDeck(deckId: number, previewToken: string, excludeWordIds?: number[]) {
    const result = await $api<{ added: boolean }>(`srs/study-decks/${deckId}/import`, {
      method: 'POST',
      body: { previewToken, excludeWordIds },
    });
    refreshOverview();
    invalidateSession();
    return result;
  }

  const activeDecks = computed(() => studyDecks.value.filter(d => d.isActive));
  const inactiveDecks = computed(() => studyDecks.value.filter(d => !d.isActive));

  async function reorderStudyDecks(reorderedDecks: StudyDeckDto[]) {
    studyDecks.value = reorderedDecks;
    const active = reorderedDecks.filter(d => d.isActive);
    const inactive = reorderedDecks.filter(d => !d.isActive);
    await $api('srs/study-decks/reorder', {
      method: 'PUT',
      body: {
        items: [
          ...active.map((d, i) => ({ userStudyDeckId: d.userStudyDeckId, sortOrder: i, isActive: true })),
          ...inactive.map((d, i) => ({ userStudyDeckId: d.userStudyDeckId, sortOrder: i, isActive: false })),
        ],
      },
    });
    invalidateSession();
  }

  async function toggleDeckActive(deckId: number) {
    const deck = studyDecks.value.find(d => d.userStudyDeckId === deckId);
    if (!deck) return;
    deck.isActive = !deck.isActive;
    await reorderStudyDecks([...studyDecks.value]);
    await fetchDueSummary();
    invalidateSession();
  }

  async function fetchBatch(limit?: number) {
    if (isWrappingUp.value) return;

    if (sessionDirty.value) {
      sessionId.value = null;
      currentBatch.value = [];
      currentCardIndex.value = 0;
      isFlipped.value = false;
      isSessionComplete.value = false;
      isWrappingUp.value = false;
      preWrapUpBatch.value = [];
      againCardKeys.value = new Set();
      clearedGrades.value = [];
      undoStack.value = [];
      inFlightReviews.clear();
      sessionEpoch++;
      exampleCache.value = new Map();
      examplePrefetchedUpTo.value = -1;
      sessionDirty.value = false;
    }

    isLoading.value = true;
    const isRefetch = sessionStats.value.cardsReviewed > 0;
    try {
      const effectiveLimit = limit ?? studySettings.value.batchSize;
      const params = new URLSearchParams({ limit: String(effectiveLimit) });
      if (sessionId.value) params.append('sessionId', sessionId.value);
      const sm = studyMoreParams.value;
      if (sm) {
        if (sm.extraNewCards) {
          const remaining = Math.max(0, sm.extraNewCards - sessionStats.value.newCardsLearned);
          if (remaining > 0) params.append('extraNewCards', String(remaining));
        }
        if (sm.extraReviews) {
          const remaining = Math.max(0, sm.extraReviews - sessionStats.value.cardsReviewed);
          if (remaining > 0) params.append('extraReviews', String(remaining));
        }
        if (sm.aheadMinutes) params.append('aheadMinutes', String(sm.aheadMinutes));
        if (sm.mistakeDays) params.append('mistakeDays', String(sm.mistakeDays));
      }
      const response = await $api<StudyBatchResponse>(`srs/study-batch?${params}`);
      sessionId.value = response.sessionId;
      if (isRefetch && response.cards.length > 0) {
        currentBatch.value = [...currentBatch.value, ...response.cards];
      } else if (isRefetch && response.cards.length === 0) {
        isSessionComplete.value = true;
      } else {
        currentBatch.value = response.cards;
        currentCardIndex.value = 0;
        isSessionComplete.value = false;
      }
      isFlipped.value = false;
      cardShownAt.value = Date.now();
      newCardsRemaining.value = response.newCardsRemaining;
      reviewsRemaining.value = response.reviewsRemaining;
      newCardsToday.value = response.newCardsToday;
      reviewsToday.value = response.reviewsToday;
      if (!sessionStats.value.startTime) {
        sessionStats.value.startTime = new Date();
      }
      fetchError.value = null;

      // Prefetch example sentences for first 4 cards (await so the first card has its example ready)
      if (currentBatch.value.length > 0)
        await prefetchExamples(currentCardIndex.value, 4);
    } catch (error) {
      console.error('Failed to fetch study batch:', error);
      fetchError.value = 'Failed to load cards. Please try again.';
    } finally {
      isLoading.value = false;
    }
  }

  function cardExampleKey(c: StudyCardDto) {
    return `${c.wordId}-${c.readingIndex}`;
  }

  function getCardExample(wordId: number, readingIndex: number): StudyExampleSentenceDto | null | undefined {
    return exampleCache.value.get(`${wordId}-${readingIndex}`);
  }

  async function prefetchExamples(fromIndex: number, count: number) {
    const end = Math.min(fromIndex + count, currentBatch.value.length);
    const pairs: { wordId: number; readingIndex: number }[] = [];

    for (let i = fromIndex; i < end; i++) {
      const c = currentBatch.value[i];
      if (!exampleCache.value.has(cardExampleKey(c)))
        pairs.push({ wordId: c.wordId, readingIndex: c.readingIndex });
    }

    if (pairs.length === 0) {
      examplePrefetchedUpTo.value = Math.max(examplePrefetchedUpTo.value, end - 1);
      return;
    }

    try {
      const response = await $api<CardExamplesResponse>('srs/card-examples', {
        method: 'POST',
        body: { pairs },
      });

      const newCache = new Map(exampleCache.value);
      for (const p of pairs) {
        const key = `${p.wordId}-${p.readingIndex}`;
        newCache.set(key, response.examples[key] ?? null);
      }
      exampleCache.value = newCache;
      examplePrefetchedUpTo.value = Math.max(examplePrefetchedUpTo.value, end - 1);
    } catch {
      // Non-blocking — examples just won't show
    }
  }

  function ensurePrefetched() {
    const ahead = 3;
    const needed = currentCardIndex.value + ahead;
    if (needed > examplePrefetchedUpTo.value) {
      prefetchExamples(examplePrefetchedUpTo.value + 1, needed - examplePrefetchedUpTo.value);
    }
  }

  function revealCard() {
    isFlipped.value = true;
    thinkingDuration.value = cardShownAt.value ? Date.now() - cardShownAt.value : undefined;
  }

  // Optimistic grading: the UI advances synchronously and the /srs/review request is sent in the
  // background. isFlipped flips to false immediately, which also acts as the per-card guard against a
  // double grade (every grade trigger requires isFlipped). isBusy is only consulted (not set) so that
  // a slower serialized action — quickAction/undo — can still block grading, but consecutive grades
  // never block each other on the network.
  function gradeCard(rating: FsrsRating): boolean {
    const card = currentCard.value;
    if (!card || isBusy.value || !isFlipped.value) return true;

    takeSnapshot(card, 'grade', rating);

    const AFK_THRESHOLD = 60_000;
    const reviewDuration = thinkingDuration.value !== undefined
      ? Math.min(thinkingDuration.value, AFK_THRESHOLD) : undefined;

    const cardKey = `${card.wordId}-${card.readingIndex}`;
    const isRepeat = againCardKeys.value.has(cardKey);
    const kanaReading = card.readings.find(r => r.formType === 1)?.text ?? card.wordTextPlain;

    const reviewEntry: SessionReview = {
      wordId: card.wordId,
      readingIndex: card.readingIndex,
      wordText: card.wordTextPlain,
      reading: kanaReading,
      rating,
      duration: reviewDuration,
    };
    sessionReviews.value = [...sessionReviews.value, reviewEntry];

    // Stats — record the exact deltas so a failed background sync can revert just this card.
    const counted = studySettings.value.countFailedReviews || !isRepeat;
    const wasNew = card.isNewCard && !isRepeat;
    const correct = rating >= FsrsRating.Good;
    const gradeKey: 'again' | 'hard' | 'good' | 'easy' =
      rating === FsrsRating.Again ? 'again'
        : rating === FsrsRating.Hard ? 'hard'
          : rating === FsrsRating.Easy ? 'easy' : 'good';
    if (counted) sessionStats.value.cardsReviewed++;
    if (wasNew) sessionStats.value.newCardsLearned++;
    if (correct) sessionStats.value.correctCount++;
    sessionStats.value.gradeCounts[gradeKey]++;

    prefetchSessionSummary();

    // Optimistic queue mutation — assume the review succeeds and the card is not a fresh leech.
    // Leech suspension and the freshly-computed interval preview are reconciled when the request lands.
    let reinsertedAgainCard: StudyCardDto | null = null;
    let clearedGrade: 'hard' | 'good' | 'easy' | null = null;
    if (rating === FsrsRating.Again) {
      const newSet = new Set(againCardKeys.value);
      newSet.add(cardKey);
      againCardKeys.value = newSet;

      const batch = [...currentBatch.value];
      batch.splice(currentCardIndex.value, 1);
      const remaining = batch.length - currentCardIndex.value;
      const offset = remaining <= 0 ? 0 : Math.min(Math.floor(Math.random() * 6) + 5, remaining);
      const reinsertedCard = { ...card };
      batch.splice(currentCardIndex.value + offset, 0, reinsertedCard);
      reinsertedAgainCard = reinsertedCard;
      currentBatch.value = batch;
    } else {
      if (isRepeat) {
        const newSet = new Set(againCardKeys.value);
        newSet.delete(cardKey);
        againCardKeys.value = newSet;
      }
      clearedGrade = rating === FsrsRating.Hard ? 'hard' : rating === FsrsRating.Easy ? 'easy' : 'good';
      clearedGrades.value = [...clearedGrades.value, clearedGrade];
      currentCardIndex.value++;
    }

    ensurePrefetched();
    isFlipped.value = false;
    cardShownAt.value = Date.now();

    const body = {
      wordId: card.wordId,
      readingIndex: card.readingIndex,
      rating,
      reviewDuration,
      sessionId: sessionId.value,
      clientRequestId: crypto.randomUUID(),
    };
    const ctx: PendingReview = {
      cardKey,
      card,
      rating,
      reviewEntry,
      reinsertedAgainCard,
      epoch: sessionEpoch,
      deltas: { counted, wasNew, correct, gradeKey, clearedGrade },
    };
    const promise = submitReview(body, ctx);
    inFlightReviews.set(cardKey, promise);
    promise.finally(() => {
      if (inFlightReviews.get(cardKey) === promise) inFlightReviews.delete(cardKey);
    });

    if (currentCardIndex.value >= currentBatch.value.length) {
      if (isWrappingUp.value) {
        isSessionComplete.value = true;
      } else {
        fetchBatch();
      }
    }
    return true;
  }

  async function submitReview(body: any, ctx: PendingReview): Promise<void> {
    let reviewResult: any;
    let failed = false;
    try {
      reviewResult = await $api('srs/review', { method: 'POST', body });
    } catch (firstError: any) {
      // 429 is the server-side debounce treating the request as a duplicate — i.e. already accepted.
      if (firstError?.status !== 429) {
        try {
          reviewResult = await $api('srs/review', { method: 'POST', body });
        } catch (retryError: any) {
          if (retryError?.status !== 429) failed = true;
        }
      }
    }

    if (ctx.epoch !== sessionEpoch) return; // session was reset; drop this late result

    if (failed) {
      console.error('Failed to persist review for', ctx.cardKey);
      handleReviewFailure(ctx);
    } else {
      applyReviewResult(reviewResult, ctx);
    }
  }

  // Reconcile the server response with the optimistic state once the review lands.
  function applyReviewResult(reviewResult: any, ctx: PendingReview): void {
    if (reviewResult?.leechDetected || reviewResult?.leechSuspended) {
      lastLeechEvent.value = { detected: true, suspended: !!reviewResult.leechSuspended };
      if (!sessionLeeches.value.some(l => `${l.wordId}-${l.readingIndex}` === ctx.cardKey)) {
        sessionLeeches.value = [...sessionLeeches.value, {
          wordId: ctx.card.wordId,
          readingIndex: ctx.card.readingIndex,
          wordText: ctx.card.wordTextPlain,
          reading: ctx.reviewEntry.reading,
          suspended: !!reviewResult.leechSuspended,
        }];
      }
    }

    if (ctx.rating === FsrsRating.Again && ctx.reinsertedAgainCard) {
      if (reviewResult?.leechSuspended) {
        // The card was suspended server-side — pull the optimistically re-queued copy back out.
        const idx = currentBatch.value.indexOf(ctx.reinsertedAgainCard);
        if (idx >= 0) {
          const batch = [...currentBatch.value];
          batch.splice(idx, 1);
          currentBatch.value = batch;
        }
        const newSet = new Set(againCardKeys.value);
        newSet.delete(ctx.cardKey);
        againCardKeys.value = newSet;
        clearedGrades.value = [...clearedGrades.value, 'action'];

        if (currentCardIndex.value >= currentBatch.value.length) {
          if (isWrappingUp.value) isSessionComplete.value = true;
          else fetchBatch();
        }
      } else if (reviewResult?.intervalPreview) {
        // Patch the freshly-scheduled interval onto the re-queued card (deep-reactive via the ref).
        ctx.reinsertedAgainCard.intervalPreview = reviewResult.intervalPreview;
      }
    }
  }

  // A review failed to persist (after one retry). Revert just this card's optimistic effects and
  // re-queue it so the user grades it again, without disturbing later cards already graded.
  function handleReviewFailure(ctx: PendingReview): void {
    const s = sessionStats.value;
    if (ctx.deltas.counted) s.cardsReviewed = Math.max(0, s.cardsReviewed - 1);
    if (ctx.deltas.wasNew) s.newCardsLearned = Math.max(0, s.newCardsLearned - 1);
    if (ctx.deltas.correct) s.correctCount = Math.max(0, s.correctCount - 1);
    s.gradeCounts[ctx.deltas.gradeKey] = Math.max(0, s.gradeCounts[ctx.deltas.gradeKey] - 1);

    sessionReviews.value = sessionReviews.value.filter(r => r !== ctx.reviewEntry);

    if (ctx.deltas.clearedGrade) {
      const i = clearedGrades.value.indexOf(ctx.deltas.clearedGrade);
      if (i >= 0) {
        const arr = [...clearedGrades.value];
        arr.splice(i, 1);
        clearedGrades.value = arr;
      }
    }

    // "Again" cards are already back in the queue; only non-Again cards need re-queuing.
    if (ctx.rating !== FsrsRating.Again) {
      const batch = [...currentBatch.value];
      const remaining = batch.length - currentCardIndex.value;
      const offset = remaining <= 0 ? 0 : Math.min(Math.floor(Math.random() * 6) + 5, remaining);
      batch.splice(currentCardIndex.value + offset, 0, { ...ctx.card });
      currentBatch.value = batch;
      isSessionComplete.value = false;
    }

    // The re-queue shifts indices, so snapshots taken after this card can no longer be trusted.
    undoStack.value = [];

    lastReviewError.value = { wordText: ctx.card.wordTextPlain };
  }

  async function quickAction(action: 'blacklist' | 'master' | 'forget' | 'suspend' | 'bury'): Promise<boolean> {
    const card = currentCard.value;
    if (!card || isBusy.value) return true;
    isBusy.value = true;

    const stateMap: Record<string, string> = {
      blacklist: 'blacklist-add',
      master: 'neverForget-add',
      forget: 'forget-add',
      suspend: 'suspend-add',
      bury: 'bury-add',
    };

    try {
      // Forget permanently deletes the card and its review logs server-side — there is no restore
      // path, so it can't be undone. Clear the stack rather than offer a misleading undo past it.
      if (action !== 'forget') takeSnapshot(card, action);
      else undoStack.value = [];

      await $api('srs/set-vocabulary-state', {
        method: 'POST',
        body: {
          wordId: card.wordId,
          readingIndex: card.readingIndex,
          state: stateMap[action],
        },
      });

      sessionStats.value.cardsReviewed++;
      clearedGrades.value = [...clearedGrades.value, 'action'];
      currentCardIndex.value++;
      isFlipped.value = false;
      cardShownAt.value = Date.now();

      ensurePrefetched();

      if (currentCardIndex.value >= currentBatch.value.length) {
        if (isWrappingUp.value) {
          isSessionComplete.value = true;
        } else {
          await fetchBatch();
        }
      }
    } catch (error) {
      // The action failed before mutating local state — drop the snapshot we optimistically pushed.
      undoStack.value = undoStack.value.slice(0, -1);
      console.error('Failed to set vocabulary state:', error);
      return false;
    } finally {
      isBusy.value = false;
    }
    return true;
  }

  async function suspendLeech(wordId: number, readingIndex: number): Promise<boolean> {
    try {
      await $api('srs/set-vocabulary-state', {
        method: 'POST',
        body: { wordId, readingIndex, state: 'suspend-add' },
      });
      sessionLeeches.value = sessionLeeches.value.map(l =>
        l.wordId === wordId && l.readingIndex === readingIndex ? { ...l, suspended: true } : l,
      );
      return true;
    } catch {
      return false;
    }
  }

  async function undoLastAction(): Promise<boolean> {
    const snap = undoStack.value[undoStack.value.length - 1];
    if (!snap || isBusy.value) return true;
    isBusy.value = true;

    try {
      if (snap.type === 'grade') {
        // Make sure the (possibly still in-flight) review for this card has landed before undoing it.
        const key = `${snap.card.wordId}-${snap.card.readingIndex}`;
        await inFlightReviews.get(key)?.catch(() => {});
        // If that review failed, it already reverted itself and cleared the stack — nothing to undo.
        if (undoStack.value[undoStack.value.length - 1] !== snap) return true;
        await $api('srs/undo-review', {
          method: 'POST',
          body: { wordId: snap.card.wordId, readingIndex: snap.card.readingIndex },
        });
      } else {
        const revertMap: Record<string, string> = {
          blacklist: 'blacklist-remove',
          suspend: 'suspend-remove',
          bury: 'bury-remove',
          master: 'neverForget-remove',
        };
        const revertState = revertMap[snap.type] ?? 'neverForget-remove';
        await $api('srs/set-vocabulary-state', {
          method: 'POST',
          body: {
            wordId: snap.card.wordId,
            readingIndex: snap.card.readingIndex,
            state: revertState,
          },
        });
      }

      currentBatch.value = snap.batch;
      currentCardIndex.value = snap.cardIndex;
      againCardKeys.value = new Set(snap.againKeys);
      clearedGrades.value = [...snap.grades];
      sessionReviews.value = [...snap.reviews];
      sessionLeeches.value = [...snap.leeches];
      sessionStats.value = {
        ...JSON.parse(JSON.stringify(snap.stats)),
        startTime: snap.stats.startTime ? new Date(snap.stats.startTime) : null,
      };
      isFlipped.value = true;
      cardShownAt.value = Date.now();
      isSessionComplete.value = false;
      undoStack.value = undoStack.value.slice(0, -1);
      return true;
    } catch (error) {
      console.error('Failed to undo:', error);
      return false;
    } finally {
      isBusy.value = false;
    }
  }

  function wrapUp() {
    if (isWrappingUp.value) return;
    isWrappingUp.value = true;
    preWrapUpBatch.value = [...currentBatch.value];

    // Keep the current card + any "again" cards still in the queue
    const upcoming = currentBatch.value.slice(currentCardIndex.value + 1);
    const keptAgain = upcoming.filter((c) => {
      const key = `${c.wordId}-${c.readingIndex}`;
      return againCardKeys.value.has(key);
    });

    const current = currentBatch.value[currentCardIndex.value];
    currentBatch.value = [
      ...currentBatch.value.slice(0, currentCardIndex.value),
      ...(current ? [current] : []),
      ...keptAgain,
    ];

    if (currentCardIndex.value >= currentBatch.value.length) {
      isSessionComplete.value = true;
    }
  }

  function cancelWrapUp() {
    if (!isWrappingUp.value) return;
    isWrappingUp.value = false;
    currentBatch.value = preWrapUpBatch.value;
    preWrapUpBatch.value = [];
  }

  async function fetchEnrollment() {
    try {
      const res = await $api<{ enrolled: boolean }>('srs/enrolled');
      srsEnrolled.value = res.enrolled;
    } catch {
      if (srsEnrolled.value !== true) {
        srsEnrolled.value = false;
      }
    }
  }

  async function enroll() {
    const res = await $api<{ enrolled: boolean }>('srs/enroll', { method: 'POST' });
    srsEnrolled.value = res.enrolled;
  }

  async function fetchSettings(force = false) {
    if (!force && settingsLoaded.value) return;
    try {
      studySettings.value = await $api<StudySettingsDto>('srs/study-settings');
      settingsLoaded.value = true;
    } catch {
      // Use defaults
    }
  }

  async function updateSettings(settings: StudySettingsDto) {
    studySettings.value = await $api<StudySettingsDto>('srs/study-settings', {
      method: 'PUT',
      body: settings,
    });
    invalidateSession();
    refreshOverview();
  }

  function clearSessionState() {
    sessionId.value = null;
    currentBatch.value = [];
    currentCardIndex.value = 0;
    isFlipped.value = false;
    isSessionComplete.value = false;
    isWrappingUp.value = false;
    preWrapUpBatch.value = [];
    againCardKeys.value = new Set();
    clearedGrades.value = [];
    sessionReviews.value = [];
    sessionLeeches.value = [];
    sessionStats.value = { cardsReviewed: 0, newCardsLearned: 0, correctCount: 0, startTime: null, gradeCounts: { again: 0, hard: 0, good: 0, easy: 0 } };
    undoStack.value = [];
    isBusy.value = false;
    fetchError.value = null;
    lastReviewError.value = null;
    inFlightReviews.clear();
    sessionEpoch++;
    exampleCache.value = new Map();
    examplePrefetchedUpTo.value = -1;
    sessionDirty.value = false;
    sessionStreak.value = null;
    sessionForecast.value = null;
    summaryPrefetched = false;
  }

  function startStudyMore(params: StudyMoreParams) {
    studyMoreParams.value = params;
    clearSessionState();
  }

  function resetSession() {
    studyMoreParams.value = null;
    clearSessionState();
  }

  return {
    studyDecks,
    activeDecks,
    inactiveDecks,
    currentBatch,
    currentCardIndex,
    isFlipped,
    isLoading,
    isSessionComplete,
    isWrappingUp,
    studySettings,
    sessionStats,
    newCardsRemaining,
    reviewsRemaining,
    newCardsToday,
    reviewsToday,
    againCardsAhead,
    remainingByType,
    clearedGrades,
    hardestCards,
    isBusy,
    fetchError,
    lastReviewError,
    dueSummary,
    deckStreak,
    reviewForecast30d,
    fetchReviewForecast30d,
    canUndo,
    currentCard,
    hasCards,
    progress,
    refreshOverview,
    fetchStudyDecks,
    fetchDueSummary,
    fetchDeckStreak,
    addStudyDeck,
    updateStudyDeck,
    removeStudyDeck,
    addDeckWord,
    removeDeckWord,
    updateDeckWordOccurrences,
    importPreview,
    importCommit,
    importPreviewText,
    importToExistingDeck,
    reorderStudyDecks,
    toggleDeckActive,
    studyMoreParams,
    getCardExample,
    fetchBatch,
    revealCard,
    gradeCard,
    quickAction,
    undoLastAction,
    startStudyMore,
    wrapUp,
    againCardKeys,
    lastLeechEvent,
    sessionLeeches,
    suspendLeech,
    cancelWrapUp,
    srsEnrolled,
    fetchEnrollment,
    enroll,
    fetchSettings,
    updateSettings,
    resetSession,
    sessionStreak,
    sessionForecast,
  };
});
