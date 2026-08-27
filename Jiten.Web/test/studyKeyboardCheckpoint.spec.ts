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

let now = 1_700_000_000_000;

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
    timeStamp: now,
    preventDefault: () => {},
    ...over,
  } as unknown as KeyboardEvent);
}

async function storeAtCheckpoint() {
  const store = useSrsStore();
  store.studySettings.batchSize = 2;
  store.studySettings.pauseBetweenBatches = true;
  await store.fetchBatch();

  store.revealCard();
  store.gradeCard(3);
  await flush();
  store.revealCard();
  store.gradeCard(3);
  await flush();
  await flush();

  expect(store.batchComplete).toBe(true);
  return store;
}

describe('batch-complete checkpoint keyboard', () => {
  beforeEach(() => {
    keydown = null;
    now = 1_700_000_000_000;
    vi.spyOn(Date, 'now').mockImplementation(() => now);
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('continues on Enter once the dwell window has passed', async () => {
    const store = await storeAtCheckpoint();
    const cb = spies();
    useStudyKeyboard(cb);

    now += 400;
    press('Enter');

    expect(cb.onContinueBatch).toHaveBeenCalledTimes(1);
    expect(cb.onEndSession).not.toHaveBeenCalled();
    expect(store.isFlipped).toBe(false);
  });

  // Grading the last card sets batchComplete in the same tick, so a fast double-tap of Enter would
  // otherwise grade Good and continue past a checkpoint the user never saw.
  it('ignores Enter inside the dwell window of the checkpoint appearing', async () => {
    await storeAtCheckpoint();
    const cb = spies();
    useStudyKeyboard(cb);

    press('Enter');
    expect(cb.onContinueBatch).not.toHaveBeenCalled();

    now += 350;
    press('Enter');
    expect(cb.onContinueBatch).toHaveBeenCalledTimes(1);
  });

  it('does not skip the checkpoint when Enter grades the last card and is tapped again', async () => {
    const store = useSrsStore();
    store.studySettings.batchSize = 2;
    store.studySettings.pauseBetweenBatches = true;
    await store.fetchBatch();

    const cb = { ...spies(), onGrade: (rating: number) => store.gradeCard(rating), onContinueBatch: vi.fn() };
    useStudyKeyboard(cb);

    let stamp = 0;
    for (let i = 0; i < 2; i++) {
      press('Enter', { timeStamp: stamp });
      stamp += 400;
      press('Enter', { timeStamp: stamp });
      stamp += 400;
      await flush();
    }
    await flush();

    expect(store.batchComplete).toBe(true);
    press('Enter', { timeStamp: stamp });
    expect(cb.onContinueBatch).not.toHaveBeenCalled();
  });

  it('ignores a held Enter', async () => {
    await storeAtCheckpoint();
    const cb = spies();
    useStudyKeyboard(cb);

    now += 400;
    press('Enter', { repeat: true });

    expect(cb.onContinueBatch).not.toHaveBeenCalled();
  });

  it('ends the session on Escape, not through wrap-up', async () => {
    await storeAtCheckpoint();
    const cb = spies();
    useStudyKeyboard(cb);

    press('Escape');

    expect(cb.onEndSession).toHaveBeenCalledTimes(1);
    expect(cb.onWrapUp).not.toHaveBeenCalled();
  });

  it('ends the session on the wrap-up keybind', async () => {
    await storeAtCheckpoint();
    const cb = spies();
    useStudyKeyboard(cb);

    press('w');

    expect(cb.onEndSession).toHaveBeenCalledTimes(1);
    expect(cb.onWrapUp).not.toHaveBeenCalled();
  });

  it('leaves no checkpoint residue when Escape ends the session', async () => {
    const store = await storeAtCheckpoint();
    const cb = { ...spies(), onEndSession: () => store.endSessionFromBatch() };
    useStudyKeyboard(cb);

    press('Escape');

    expect(store.isSessionComplete).toBe(true);
    expect(store.batchComplete).toBe(false);
    expect(store.isWrappingUp).toBe(false);
  });

  it('swallows every other study key at the checkpoint', async () => {
    await storeAtCheckpoint();
    const cb = spies();
    useStudyKeyboard(cb);

    for (const key of ['1', '2', '3', '4', ' ', 'z', 'p', 'r', 'b', 'f', 'm', 's', 'h']) press(key);

    for (const [name, spy] of Object.entries(cb)) {
      expect(spy, name).not.toHaveBeenCalled();
    }
  });

  it('leaves mid-card keys unchanged', async () => {
    const store = useSrsStore();
    store.studySettings.batchSize = 2;
    store.studySettings.pauseBetweenBatches = true;
    await store.fetchBatch();

    const cb = spies();
    useStudyKeyboard(cb);

    press('Enter', { timeStamp: 0 });
    expect(store.isFlipped).toBe(true);
    expect(cb.onGrade).not.toHaveBeenCalled();

    press('Enter', { timeStamp: 400 });
    expect(cb.onGrade).toHaveBeenCalledTimes(1);

    press('Escape');
    expect(cb.onWrapUp).toHaveBeenCalledTimes(1);
    expect(cb.onEndSession).not.toHaveBeenCalled();
  });
});
