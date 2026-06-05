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
};

export function displayKeyName(key: string): string {
  switch (key) {
    case ' ': return 'Space';
    case 'ArrowUp': return '↑';
    case 'ArrowDown': return '↓';
    case 'ArrowLeft': return '←';
    case 'ArrowRight': return '→';
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

function matchesKeybind(e: KeyboardEvent, boundKey: string): boolean {
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

  function flashKey(key: string) {
    pressedKey.value = key;
    if (pressedTimeout) clearTimeout(pressedTimeout);
    pressedTimeout = setTimeout(() => { pressedKey.value = null; }, 150);
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.repeat) return;
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    if (store.isBusy) return;

    const kb = store.studySettings.keybinds ?? DEFAULT_KEYBINDS;
    const is4Btn = store.studySettings.gradingButtons === 4;
    const gradeKeys = is4Btn
      ? [kb.grade1, kb.grade2, kb.grade3, kb.grade4]
      : [kb.grade1, kb.grade2];
    const ratings = is4Btn ? RATINGS_4 : RATINGS_2;

    if (e.key === 'Escape') {
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

    if (matchesKeybind(e, kb.flipCard) || e.key === 'Enter') {
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
      if (matchesKeybind(e, kb.blacklist)) { flashKey(kb.blacklist); callbacks.onBlacklist(); return; }
      if (matchesKeybind(e, kb.forget)) { flashKey(kb.forget); callbacks.onForget(); return; }
      if (matchesKeybind(e, kb.master)) { flashKey(kb.master); callbacks.onMaster(); return; }
      if (matchesKeybind(e, kb.suspend)) { flashKey(kb.suspend); callbacks.onSuspend(); return; }
      if (matchesKeybind(e, kb.bury)) { flashKey(kb.bury); callbacks.onBury(); return; }
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

  onMounted(() => {
    window.addEventListener('keydown', handleKeydown);
  });

  onUnmounted(() => {
    window.removeEventListener('keydown', handleKeydown);
  });

  return { pressedKey: readonly(pressedKey) };
}
