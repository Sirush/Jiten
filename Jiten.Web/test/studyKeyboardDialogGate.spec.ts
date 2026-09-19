import { computed, readonly, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';

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

let openOverlay: object | null = null;
vi.stubGlobal('document', {
  querySelector: (selector: string) => {
    expect(selector).toContain('[role="dialog"][aria-modal="true"]');
    expect(selector).toContain('[role="alertdialog"]');
    return openOverlay;
  },
});

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
  const preventDefault = vi.fn();
  keydown!({
    key,
    code,
    repeat: false,
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    target: null,
    timeStamp: 10_000,
    preventDefault,
    ...over,
  } as unknown as KeyboardEvent);
  return preventDefault;
}

async function flippedStore() {
  const store = useSrsStore();
  await store.fetchBatch();
  store.revealCard();
  return store;
}

describe('study keyboard with an open dialog', () => {
  beforeEach(() => {
    keydown = null;
    openOverlay = null;
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  // Forget opens a ConfirmDialog with Accept focused; Enter must activate that button, not grade Good.
  it('lets Enter reach the confirm button instead of grading', async () => {
    await flippedStore();
    const cb = spies();
    useStudyKeyboard(cb);

    openOverlay = {};
    const preventDefault = press('Enter');

    expect(cb.onGrade).not.toHaveBeenCalled();
    expect(preventDefault).not.toHaveBeenCalled();
  });

  it('does not toggle wrap-up when Escape closes a dialog', async () => {
    await flippedStore();
    const cb = spies();
    useStudyKeyboard(cb);

    openOverlay = {};
    press('Escape');

    expect(cb.onWrapUp).not.toHaveBeenCalled();
  });

  it('ignores grade and action keys while a dialog is open', async () => {
    await flippedStore();
    const cb = spies();
    useStudyKeyboard(cb);

    openOverlay = {};
    press('3');
    press('f');
    press(' ');

    expect(cb.onGrade).not.toHaveBeenCalled();
    expect(cb.onForget).not.toHaveBeenCalled();
  });

  it('resumes handling once the dialog is gone', async () => {
    await flippedStore();
    const cb = spies();
    useStudyKeyboard(cb);

    openOverlay = {};
    press('3');
    openOverlay = null;
    press('3');

    expect(cb.onGrade).toHaveBeenCalledTimes(1);
  });
});
