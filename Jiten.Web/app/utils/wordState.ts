import { KnownState } from '~/types';

/** Word colour keys; a null colour means the ordinary text colour. */
export type WordStateColourKey = 'new' | 'young' | 'due' | 'mature' | 'redundant' | 'ignored';

export type WordStateColours = Record<WordStateColourKey, string | null>;

export const WORD_STATE_COLOUR_KEYS: WordStateColourKey[] = ['new', 'young', 'due', 'mature', 'redundant', 'ignored'];

export const DEFAULT_WORD_STATE_COLOURS: WordStateColours = {
  new: '#f43f5e',
  young: '#f59e0b',
  due: '#f97316',
  mature: null,
  redundant: '#0ea5e9',
  ignored: '#9ca3af',
};

export const WORD_STATE_COLOUR_LABELS: Record<WordStateColourKey, string> = {
  new: 'Unknown',
  young: 'Young',
  due: 'Due',
  mature: 'Mature / Mastered',
  redundant: 'Redundant',
  ignored: 'Blacklisted / Suspended',
};

export function wordStateColourKey(states: KnownState[] | undefined | null): WordStateColourKey {
  if (!states || states.length === 0) return 'new';
  if (states.includes(KnownState.Blacklisted) || states.includes(KnownState.Suspended)) return 'ignored';
  if (states.includes(KnownState.Redundant)) return 'redundant';
  if (states.includes(KnownState.Mastered) || states.includes(KnownState.Mature)) return 'mature';
  if (states.includes(KnownState.Due)) return 'due';
  if (states.includes(KnownState.Young)) return 'young';
  return 'new';
}

/** Same rule as SentenceComprehension.IsKnown on the API, so headwords and sentences agree on which words lose their furigana. */
export function isKnownForFurigana(states: KnownState[] | undefined | null): boolean {
  if (!states) return false;
  return states.some(
    (s) => s === KnownState.Young || s === KnownState.Mature || s === KnownState.Mastered || s === KnownState.Redundant || s === KnownState.Blacklisted
  );
}

export function wordColourStyle(hex: string): string {
  return `color:${hex};color:oklch(from ${hex} clamp(var(--word-colour-min-l), l, var(--word-colour-max-l)) c h)`;
}

export function resolveWordStateColours(stored: Partial<WordStateColours> | null | undefined): WordStateColours {
  return { ...DEFAULT_WORD_STATE_COLOURS, ...(stored ?? {}) };
}
