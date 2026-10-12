import { computed, nextTick, readonly, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';
import { FsrsRating } from '../app/types';

vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('readonly', readonly);
vi.stubGlobal('nextTick', nextTick);
vi.stubGlobal('onMounted', () => {});
vi.stubGlobal('onUnmounted', () => {});
vi.stubGlobal('trackActivation', () => {});
vi.stubGlobal('trackEvent', () => {});

function card(wordId: number): StudyCardDto {
  return {
    cardId: 0,
    wordId,
    readingIndex: 0,
    state: 2,
    isNewCard: false,
    lapses: 0,
    isLeech: false,
    wordText: `word${wordId}`,
    wordTextPlain: `word${wordId}`,
    readings: [],
    definitions: [],
    partsOfSpeech: [],
    frequencyRank: 0,
    lastReview: '2026-01-01T00:00:00Z',
    intervalPreview: { againSeconds: 0, hardSeconds: 86400, goodSeconds: 86400 * 30, easySeconds: 86400 * 60 },
  } as unknown as StudyCardDto;
}

let batchCards: StudyCardDto[] = [];
const $api = vi.fn((path: string) => {
  if (path.startsWith('srs/study-batch')) {
    return Promise.resolve({ sessionId: 'session-1', cards: batchCards, newCardsRemaining: 0, reviewsRemaining: 0, newCardsToday: 0, reviewsToday: 0 });
  }
  if (path === 'srs/review') return Promise.resolve({ newState: 2 });
  if (path === 'srs/card-examples') return Promise.resolve({ examples: {} });
  return Promise.resolve({});
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));

const { useSrsStore } = await import('../app/stores/srsStore');
const { useStudyTimer } = await import('../app/composables/useStudyTimer');

async function start(timed: Partial<ReturnType<typeof useSrsStore>['studySettings']['timedReview']> = {}) {
  const store = useSrsStore();
  store.studySettings.pauseBetweenBatches = false;
  Object.assign(store.studySettings.timedReview, { skipNewCards: false, alertSound: false, ...timed });
  const graded: FsrsRating[] = [];
  const timer = useStudyTimer({
    active: ref(true),
    onReveal: () => store.revealCard(),
    onGrade: (rating) => {
      graded.push(rating);
      store.gradeCard(rating);
    },
  });
  batchCards = [card(1), card(2), card(3)];
  await store.fetchBatch();
  await vi.advanceTimersByTimeAsync(0);
  return { store, timer, graded };
}

async function armSoftFail(store: ReturnType<typeof useSrsStore>, timer: ReturnType<typeof useStudyTimer>) {
  store.revealCard();
  await vi.advanceTimersByTimeAsync(4_100);
  expect(timer.phase.value).toBe('armed');
}

describe('study timer', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.useFakeTimers();
  });
  afterEach(() => vi.useRealTimers());

  it('a manual grade during the soft-fail grace does not carry the countdown to the next card', async () => {
    const { store, timer, graded } = await start();
    await armSoftFail(store, timer);

    await vi.advanceTimersByTimeAsync(1_000);
    store.gradeCard(FsrsRating.Good);
    await nextTick();

    expect(store.currentCard?.wordId).toBe(2);
    expect(timer.phase.value).toBe('reveal');

    store.revealCard();
    await vi.advanceTimersByTimeAsync(3_000);
    expect(graded).toEqual([]);
  });

  it('undo during the soft-fail grace restarts the answer timer on the restored card', async () => {
    const { store, timer, graded } = await start();
    store.revealCard();
    store.gradeCard(FsrsRating.Good);
    await vi.advanceTimersByTimeAsync(0);
    await armSoftFail(store, timer);

    await store.undoLastAction();
    await nextTick();

    expect(store.currentCard?.wordId).toBe(1);
    expect(timer.phase.value).toBe('answer');
    expect(graded).toEqual([]);
  });

  it('keeps the arm through a fail-learn forced reveal', async () => {
    const { timer, graded } = await start({ revealAction: 'FailLearn' });

    await vi.advanceTimersByTimeAsync(9_100);
    expect(timer.phase.value).toBe('armed');
    expect(timer.armedLocked.value).toBe(true);

    await vi.advanceTimersByTimeAsync(3_100);
    expect(graded).toEqual([FsrsRating.Again]);
  });

  it('gives only the first card the extra question time', async () => {
    const { store, timer } = await start({ answerEnabled: false });
    expect(timer.remainingMs.value).toBe(9_000);

    await vi.advanceTimersByTimeAsync(1_000);
    store.revealCard();
    store.gradeCard(FsrsRating.Good);
    await nextTick();

    expect(timer.remainingMs.value).toBe(8_000);
  });
});
