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

type Listener = (e: Event) => void;
const listeners: Record<string, Listener | undefined> = {};
vi.stubGlobal('window', {
  addEventListener: (type: string, fn: Listener) => {
    listeners[type] = fn;
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
const { useStudyKeyboard, displayKeyName, normalizeKey, mouseToken, isMouseToken, DEFAULT_KEYBINDS } = await import(
  '../app/composables/useStudyKeyboard'
);

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

type FakeMouseEvent = MouseEvent & { preventDefault: ReturnType<typeof vi.fn> };

function mouseEvent(button: number, over: Record<string, unknown> = {}): FakeMouseEvent {
  return {
    button,
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    target: null,
    timeStamp: 0,
    preventDefault: vi.fn(),
    ...over,
  } as unknown as FakeMouseEvent;
}

function pressButton(button: number, type = 'mousedown', over: Record<string, unknown> = {}) {
  const e = mouseEvent(button, over);
  listeners[type]!(e);
  return e;
}

async function storeWithBatch() {
  const store = useSrsStore();
  store.studySettings.batchSize = 2;
  store.studySettings.pauseBetweenBatches = true;
  await store.fetchBatch();
  return store;
}

describe('mouse tokens', () => {
  it('maps bindable buttons to tokens and labels', () => {
    expect(mouseToken(0)).toBeNull();
    expect(mouseToken(5)).toBeNull();
    expect(mouseToken(1)).toBe('Mouse1');
    expect(isMouseToken('Mouse4')).toBe(true);
    expect(isMouseToken('m')).toBe(false);
    expect(displayKeyName('Mouse1')).toBe('Middle click');
    expect(displayKeyName('Mouse2')).toBe('Right click');
    expect(displayKeyName('Mouse3')).toBe('Back button');
    expect(displayKeyName('Mouse4')).toBe('Forward button');
  });

  it('leaves keyboard normalisation and display unchanged', () => {
    expect(normalizeKey({ code: 'Digit3', key: '3' } as KeyboardEvent)).toBe('3');
    expect(normalizeKey({ code: 'Numpad3', key: '3' } as KeyboardEvent)).toBe('3');
    expect(normalizeKey({ code: 'KeyB', key: 'B' } as KeyboardEvent)).toBe('b');
    expect(normalizeKey({ code: 'Space', key: ' ' } as KeyboardEvent)).toBe(' ');
    expect(normalizeKey({ code: 'ArrowLeft', key: 'ArrowLeft' } as KeyboardEvent)).toBe('ArrowLeft');
    expect(displayKeyName(' ')).toBe('Space');
    expect(displayKeyName('b')).toBe('B');
    expect(displayKeyName('ArrowLeft')).toBe('←');
  });
});

describe('mouse bindings during review', () => {
  beforeEach(() => {
    for (const k of Object.keys(listeners)) listeners[k] = undefined;
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('grades on a bound button once the card is flipped, not before', async () => {
    const store = await storeWithBatch();
    store.studySettings.keybinds = { ...DEFAULT_KEYBINDS, grade1: 'Mouse3' };
    const cb = spies();
    useStudyKeyboard(cb);

    const before = pressButton(3);
    expect(cb.onGrade).not.toHaveBeenCalled();
    expect(before.preventDefault).toHaveBeenCalled();

    store.revealCard();
    pressButton(3);
    expect(cb.onGrade).toHaveBeenCalledWith(1);
  });

  it('flips on a bound button and grades Good after the dwell window', async () => {
    const store = await storeWithBatch();
    store.studySettings.keybinds = { ...DEFAULT_KEYBINDS, flipCard: 'Mouse1' };
    const cb = spies();
    useStudyKeyboard(cb);

    pressButton(1, 'mousedown', { timeStamp: 0 });
    expect(store.isFlipped).toBe(true);
    pressButton(1, 'mousedown', { timeStamp: 100 });
    expect(cb.onGrade).not.toHaveBeenCalled();
    pressButton(1, 'mousedown', { timeStamp: 400 });
    expect(cb.onGrade).toHaveBeenCalledWith(3);
  });

  it('ignores an unbound button and leaves its default alone', async () => {
    await storeWithBatch();
    const cb = spies();
    useStudyKeyboard(cb);

    for (const button of [0, 1, 2, 3, 4]) {
      expect(pressButton(button).preventDefault).not.toHaveBeenCalled();
      expect(pressButton(button, 'mouseup').preventDefault).not.toHaveBeenCalled();
      expect(pressButton(button, 'auxclick').preventDefault).not.toHaveBeenCalled();
    }
    expect(pressButton(2, 'contextmenu').preventDefault).not.toHaveBeenCalled();
    for (const [name, spy] of Object.entries(cb)) expect(spy, name).not.toHaveBeenCalled();
  });

  it('suppresses the context menu and follow-up events only for a bound button', async () => {
    const store = await storeWithBatch();
    store.studySettings.keybinds = { ...DEFAULT_KEYBINDS, undo: 'Mouse2' };
    useStudyKeyboard(spies());

    expect(pressButton(2, 'contextmenu').preventDefault).toHaveBeenCalled();
    expect(pressButton(2, 'mouseup').preventDefault).toHaveBeenCalled();
    expect(pressButton(2, 'auxclick').preventDefault).toHaveBeenCalled();
    expect(pressButton(1, 'mouseup').preventDefault).not.toHaveBeenCalled();
  });

  it('respects isBusy and gradeLock', async () => {
    const store = await storeWithBatch();
    store.studySettings.keybinds = { ...DEFAULT_KEYBINDS, grade3: 'Mouse4' };
    const cb = spies();
    useStudyKeyboard(cb);
    store.revealCard();

    store.gradeLock = true;
    pressButton(4);
    expect(cb.onGrade).not.toHaveBeenCalled();
    store.gradeLock = false;

    store.isBusy = true;
    pressButton(4);
    expect(cb.onGrade).not.toHaveBeenCalled();
    store.isBusy = false;

    pressButton(4);
    expect(cb.onGrade).toHaveBeenCalledTimes(1);
  });

  it('flashes the bound token for the on-screen hint', async () => {
    const store = await storeWithBatch();
    store.studySettings.keybinds = { ...DEFAULT_KEYBINDS, grade2: 'Mouse1' };
    const { pressedKey } = useStudyKeyboard(spies());
    store.revealCard();

    pressButton(1);
    expect(pressedKey.value).toBe('Mouse1');
  });

  it('at the checkpoint swallows every mouse binding except wrap-up', async () => {
    const store = await storeWithBatch();
    store.studySettings.keybinds = { ...DEFAULT_KEYBINDS, grade1: 'Mouse1', undo: 'Mouse3', wrapUp: 'Mouse4' };
    const cb = spies();
    useStudyKeyboard(cb);

    store.revealCard();
    store.gradeCard(3);
    await flush();
    store.revealCard();
    store.gradeCard(3);
    await flush();
    await flush();
    expect(store.batchComplete).toBe(true);

    pressButton(1);
    pressButton(3);
    expect(cb.onGrade).not.toHaveBeenCalled();
    expect(cb.onUndo).not.toHaveBeenCalled();

    pressButton(4);
    expect(cb.onEndSession).toHaveBeenCalledTimes(1);
    expect(cb.onWrapUp).not.toHaveBeenCalled();
  });
});
