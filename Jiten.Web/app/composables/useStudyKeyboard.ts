import type { StudyKeybinds } from '~/types';
import { FsrsRating } from '~/types';
import { useSrsStore } from '~/stores/srsStore';

export const DEFAULT_KEYBINDS: StudyKeybinds = {
  grade1: '1',
  grade2: '2',
  grade3: '3',
  grade4: '4',
  flipCard: ' ',
  blacklist: 'b',
  forget: 'f',
  master: 'm',
  suspend: 's',
  bury: 'h',
  undo: 'z',
  wrapUp: 'w',
  pauseTimer: 'p',
  replayAudio: 'r',
  dictPrev: 'ArrowLeft',
  dictNext: 'ArrowRight',
};

const MOUSE_TOKEN_PREFIX = 'Mouse';

const MOUSE_LABELS: Record<string, string> = {
  Mouse1: 'Middle click',
  Mouse2: 'Right click',
  Mouse3: 'Back button',
  Mouse4: 'Forward button',
};

export function mouseToken(button: number): string | null {
  return button >= 1 && button <= 4 ? `${MOUSE_TOKEN_PREFIX}${button}` : null;
}

export function isMouseToken(key: string): boolean {
  return key in MOUSE_LABELS;
}

export function displayKeyName(key: string): string {
  if (isMouseToken(key)) return MOUSE_LABELS[key]!;
  switch (key) {
    case ' ':
      return 'Space';
    case 'ArrowUp':
      return '↑';
    case 'ArrowDown':
      return '↓';
    case 'ArrowLeft':
      return '←';
    case 'ArrowRight':
      return '→';
    default:
      return key.length === 1 ? key.toUpperCase() : key;
  }
}

export function normalizeKey(e: KeyboardEvent): string {
  if (e.code.startsWith('Digit')) return e.code.slice(5);
  if (e.code.startsWith('Numpad') && e.code.length === 7) return e.code.slice(6);
  if (e.key === ' ') return ' ';
  if (e.key.length === 1) return e.key.toLowerCase();
  return e.key;
}

const OPEN_OVERLAY_SELECTOR = '[role="dialog"][aria-modal="true"], [role="alertdialog"]';

function hasOpenOverlay(): boolean {
  return !!globalThis.document?.querySelector(OPEN_OVERLAY_SELECTOR);
}

type StudyInput = KeyboardEvent | MouseEvent;

function isMouse(e: StudyInput): e is MouseEvent {
  return 'button' in e;
}

function matchesKeybind(e: StudyInput, boundKey: string): boolean {
  if (isMouseToken(boundKey)) return isMouse(e) && mouseToken(e.button) === boundKey;
  if (isMouse(e)) return false;
  if (/^[0-9]$/.test(boundKey)) {
    return e.code === `Digit${boundKey}` || e.code === `Numpad${boundKey}`;
  }
  if (boundKey === ' ') return e.key === ' ';
  return e.key.toLowerCase() === boundKey.toLowerCase();
}

export interface StudyKeyboardCallbacks {
  onGrade: (rating: FsrsRating) => void;
  onBlacklist: () => void;
  onForget: () => void;
  onMaster: () => void;
  onSuspend: () => void;
  onBury: () => void;
  onUndo: () => void;
  onWrapUp: () => void;
  onPauseTimer: () => void;
  onReplayAudio: () => void;
  onDictPrev: () => void;
  onDictNext: () => void;
  onContinueBatch: () => void;
  onEndSession: () => void;
}

const RATINGS_4 = [FsrsRating.Again, FsrsRating.Hard, FsrsRating.Good, FsrsRating.Easy];
const RATINGS_2 = [FsrsRating.Again, FsrsRating.Good];

export function useStudyKeyboard(callbacks: StudyKeyboardCallbacks) {
  const store = useSrsStore();
  const pressedKey = ref<string | null>(null);
  let pressedTimeout: ReturnType<typeof setTimeout> | null = null;

  // Dwell guard: ignore an auto-"Good" from the same Space/Enter that just revealed the card,
  // so a double-tapped reveal can't silently grade Good.
  const REVEAL_DWELL_MS = 350;
  let revealedAt = 0;

  let batchCompletedAt = 0;
  const stopBatchWatch = watch(
    () => store.batchComplete,
    (complete) => {
      if (complete) batchCompletedAt = Date.now();
    },
    { flush: 'sync', immediate: true }
  );

  function flashKey(key: string) {
    pressedKey.value = key;
    if (pressedTimeout) clearTimeout(pressedTimeout);
    pressedTimeout = setTimeout(() => {
      pressedKey.value = null;
    }, 150);
  }

  function keybinds(): StudyKeybinds {
    return store.studySettings.keybinds ?? DEFAULT_KEYBINDS;
  }

  function isMouseButtonBound(button: number): boolean {
    const token = mouseToken(button);
    return token !== null && Object.values(keybinds()).includes(token);
  }

  function handleInput(e: StudyInput) {
    if (isMouse(e)) {
      if (!isMouseButtonBound(e.button)) return;
      e.preventDefault();
    } else if (e.repeat) {
      return;
    }
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    if (hasOpenOverlay()) return;
    if (store.isBusy) return;
    // Timed-review "fail & learn" absorption window locks out grading while the answer is studied.
    if (store.gradeLock) return;

    const kb = keybinds();
    const key = isMouse(e) ? null : e.key;
    const is4Btn = store.studySettings.gradingButtons === 4;
    const gradeKeys = is4Btn ? [kb.grade1, kb.grade2, kb.grade3, kb.grade4] : [kb.grade1, kb.grade2];
    const ratings = is4Btn ? RATINGS_4 : RATINGS_2;

    // Swallows every other study key: replayAudio, pauseTimer, undo and wrapUp are not card-gated and would fire against a finished batch.
    if (store.batchComplete) {
      if (key === 'Enter') {
        // Owns Enter outright so the focused Continue button cannot activate a second time.
        e.preventDefault();
        if (Date.now() - batchCompletedAt >= REVEAL_DWELL_MS) callbacks.onContinueBatch();
      } else if (key === 'Escape' || matchesKeybind(e, kb.wrapUp)) {
        callbacks.onEndSession();
      }
      return;
    }

    if (key === 'Escape') {
      flashKey(kb.wrapUp);
      callbacks.onWrapUp();
      return;
    }

    if (store.isFlipped) {
      for (let i = 0; i < gradeKeys.length; i++) {
        if (matchesKeybind(e, gradeKeys[i])) {
          flashKey(gradeKeys[i]);
          callbacks.onGrade(ratings[i]);
          return;
        }
      }
    }

    if (matchesKeybind(e, kb.flipCard) || key === 'Enter') {
      e.preventDefault();
      if (!store.isFlipped) {
        flashKey(kb.flipCard);
        revealedAt = e.timeStamp;
        store.revealCard();
      } else if (e.timeStamp - revealedAt >= REVEAL_DWELL_MS) {
        const goodKey = is4Btn ? kb.grade3 : kb.grade2;
        flashKey(goodKey);
        callbacks.onGrade(FsrsRating.Good);
      }
      return;
    }

    if (store.currentCard && store.isFlipped) {
      if (matchesKeybind(e, kb.blacklist)) {
        flashKey(kb.blacklist);
        callbacks.onBlacklist();
        return;
      }
      if (matchesKeybind(e, kb.forget)) {
        flashKey(kb.forget);
        callbacks.onForget();
        return;
      }
      if (matchesKeybind(e, kb.master)) {
        flashKey(kb.master);
        callbacks.onMaster();
        return;
      }
      if (matchesKeybind(e, kb.suspend)) {
        flashKey(kb.suspend);
        callbacks.onSuspend();
        return;
      }
      if (matchesKeybind(e, kb.bury)) {
        flashKey(kb.bury);
        callbacks.onBury();
        return;
      }
      const onTabHeader = e.target instanceof HTMLElement && !!e.target.closest('[role="tab"]');
      if (!onTabHeader && matchesKeybind(e, kb.dictPrev ?? DEFAULT_KEYBINDS.dictPrev)) {
        callbacks.onDictPrev();
        return;
      }
      if (!onTabHeader && matchesKeybind(e, kb.dictNext ?? DEFAULT_KEYBINDS.dictNext)) {
        callbacks.onDictNext();
        return;
      }
    }

    const replayKey = kb.replayAudio ?? DEFAULT_KEYBINDS.replayAudio;
    if (matchesKeybind(e, replayKey)) {
      flashKey(replayKey);
      callbacks.onReplayAudio();
      return;
    }

    if (matchesKeybind(e, kb.pauseTimer)) {
      flashKey(kb.pauseTimer);
      callbacks.onPauseTimer();
      return;
    }

    if (store.canUndo && matchesKeybind(e, kb.undo)) {
      flashKey(kb.undo);
      callbacks.onUndo();
      return;
    }

    if (matchesKeybind(e, kb.wrapUp)) {
      flashKey(kb.wrapUp);
      callbacks.onWrapUp();
      return;
    }
  }

  function suppressBoundDefault(e: MouseEvent) {
    if (isMouseButtonBound(e.button)) e.preventDefault();
  }

  function handleContextMenu(e: MouseEvent) {
    if (isMouseButtonBound(2)) e.preventDefault();
  }

  onMounted(() => {
    window.addEventListener('keydown', handleInput);
    window.addEventListener('mousedown', handleInput);
    window.addEventListener('mouseup', suppressBoundDefault);
    window.addEventListener('auxclick', suppressBoundDefault);
    window.addEventListener('contextmenu', handleContextMenu);
  });

  onUnmounted(() => {
    window.removeEventListener('keydown', handleInput);
    window.removeEventListener('mousedown', handleInput);
    window.removeEventListener('mouseup', suppressBoundDefault);
    window.removeEventListener('auxclick', suppressBoundDefault);
    window.removeEventListener('contextmenu', handleContextMenu);
    stopBatchWatch();
  });

  return { pressedKey: readonly(pressedKey) };
}
