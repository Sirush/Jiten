import { computed, readonly, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';

// The store and the composable rely on Nuxt auto-imports; provide the ones they touch, plus the DOM
// constructors the keydown guards test with `instanceof` (the suite runs in the node environment).
vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('readonly', readonly);
vi.stubGlobal('trackActivation', () => {});
vi.stubGlobal('trackEvent', () => {});
class NotAnInput {
  readonly stub = true;
}
vi.stubGlobal('HTMLInputElement', NotAnInput);
vi.stubGlobal('HTMLTextAreaElement', NotAnInput);
vi.stubGlobal('HTMLElement', NotAnInput);

let keydown: ((e: KeyboardEvent) => void) | null = null;
vi.stubGlobal('window', {
  addEventListener: (type: string, fn: (e: KeyboardEvent) => void) => {
    if (type === 'keydown') keydown = fn;
  },
  removeEventListener: () => {},
});
vi.stubGlobal('onMounted', (fn: () => void) => fn());
vi.stubGlobal('onUnmounted', () => {});

function card(wordId: number): StudyCardDto {
  return {
    cardId: 0,
    wordId,
    readingIndex: 0,
    state: 0,
    isNewCard: true,
    lapses: 0,
    isLeech: false,
    wordText: `word${wordId}`,
    wordTextPlain: `word${wordId}`,
    readings: [],
    definitions: [],
    partsOfSpeech: [],
    frequencyRank: 0,
  } as unknown as StudyCardDto;
}

const $api = vi.fn((path: string) => {
  if (path.startsWith('srs/study-batch')) {
    return Promise.resolve({
      sessionId: 'session-1',
      cards: [card(1), card(2)],
      newCardsRemaining: 5,
      reviewsRemaining: 0,
      newCardsToday: 0,
      reviewsToday: 0,
    });
  }
  if (path === 'srs/card-examples') return Promise.resolve({ examples: {} });
  return Promise.resolve({});
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));

const { useSrsStore } = await import('../app/stores/srsStore');
const { useStudyKeyboard } = await import('../app/composables/useStudyKeyboard');

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

function spies() {
  return {
    onGrade: vi.fn(),
    onBlacklist: vi.fn(),
    onForget: vi.fn(),
    onMaster: vi.fn(),
    onSuspend: vi.fn(),
    onBury: vi.fn(),
    onUndo: vi.fn(),
    onWrapUp: vi.fn(),
    onPauseTimer: vi.fn(),
    onReplayAudio: vi.fn(),
    onDictPrev: vi.fn(),
    onDictNext: vi.fn(),
    onContinueBatch: vi.fn(),
    onEndSession: vi.fn(),
  };
}

function press(key: string, over: Record<string, unknown> = {}) {
  const code = /^[0-9]$/.test(key) ? `Digit${key}` : key.length === 1 ? `Key${key.toUpperCase()}` : key;
  keydown!({
    key,
    code,
    repeat: false,
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    target: null,
    timeStamp: 0,
    preventDefault: () => {},
    ...over,
  } as unknown as KeyboardEvent);
}

async function revealedStore() {
  const store = useSrsStore();
  await store.fetchBatch();
  return store;
}

describe('study keyboard on a write-in reveal', () => {
  beforeEach(() => {
    keydown = null;
    setActivePinia(createPinia());
    vi.spyOn(performance, 'now').mockReturnValue(1000);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('commits the suggested Again on Enter instead of Good', async () => {
    const store = await revealedStore();
    const cb = { ...spies(), suggestedRating: () => 1 as const };
    useStudyKeyboard(cb);
    store.revealCard();

    press('Enter', { timeStamp: 2000 });

    expect(cb.onGrade).toHaveBeenCalledWith(1);
  });

  it('commits the suggested Again on Space with two grading buttons', async () => {
    const store = await revealedStore();
    store.studySettings.gradingButtons = 2;
    const cb = { ...spies(), suggestedRating: () => 1 as const };
    useStudyKeyboard(cb);
    store.revealCard();

    press(' ', { code: 'Space', timeStamp: 2000 });

    expect(cb.onGrade).toHaveBeenCalledWith(1);
  });

  it('grades Good when nothing is suggested', async () => {
    const store = await revealedStore();
    const cb = { ...spies(), suggestedRating: () => null };
    useStudyKeyboard(cb);
    store.revealCard();

    press('Enter', { timeStamp: 2000 });

    expect(cb.onGrade).toHaveBeenCalledWith(3);
  });

  // The write-in submit reveals from inside the input, so the global handler never sees that keypress.
  it('ignores Enter inside the dwell window of a reveal it did not handle', async () => {
    const store = await revealedStore();
    const cb = { ...spies(), suggestedRating: () => 1 as const };
    useStudyKeyboard(cb);
    store.revealCard();

    press('Enter', { timeStamp: 1100 });
    expect(cb.onGrade).not.toHaveBeenCalled();

    press('Enter', { timeStamp: 1400 });
    expect(cb.onGrade).toHaveBeenCalledWith(1);
  });
});
