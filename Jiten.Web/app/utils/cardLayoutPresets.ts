import type { CardBlockOptions, CardBlockType, CardLayout, CardLayoutBlock, StudySettingsDto } from '~/types';
import { ALL_CARD_BLOCK_TYPES, buildLayoutFromLegacySettings, newBlockId } from './cardLayout';
import { DEFAULT_CARD_DISPLAY_SETTINGS } from './defaultStudySettings';
import {
  cardImageDefaults,
  confusableReadingsDefaults,
  customMeaningDefaults,
  deckOccurrencesDefaults,
  definitionsDefaults,
  dividerDefaults,
  etymologyDefaults,
  exampleSentenceDefaults,
  frequencyRankDefaults,
  headwordDefaults,
  kanjiBreakdownDefaults,
  pitchAccentDefaults,
  wordCompositionDefaults,
  wordUsedInDefaults,
} from '../components/srs/card-blocks/cardBlockOptions';

export interface BuiltInPreset {
  name: string;
  layout: CardLayout;
}

export const SHARE_CODE_PREFIX = 'jitenlayout1.';
const MAX_BLOCKS_PER_SIDE = 30;

const VALID_TYPES: ReadonlySet<string> = new Set(ALL_CARD_BLOCK_TYPES);

// The option keys each block type actually stores, derived from the registry's option defaults so a
// share code can never smuggle in unknown keys. Types with no configurable options accept none.
const OPTION_KEYS: Partial<Record<CardBlockType, ReadonlySet<string>>> = {
  headword: new Set(Object.keys(headwordDefaults)),
  exampleSentence: new Set(Object.keys(exampleSentenceDefaults)),
  frequencyRank: new Set(Object.keys(frequencyRankDefaults)),
  definitions: new Set(Object.keys(definitionsDefaults)),
  customMeaning: new Set(Object.keys(customMeaningDefaults)),
  etymology: new Set(Object.keys(etymologyDefaults)),
  confusableReadings: new Set(Object.keys(confusableReadingsDefaults)),
  pitchAccent: new Set(Object.keys(pitchAccentDefaults)),
  kanjiBreakdown: new Set(Object.keys(kanjiBreakdownDefaults)),
  wordComposition: new Set(Object.keys(wordCompositionDefaults)),
  wordUsedIn: new Set(Object.keys(wordUsedInDefaults)),
  deckOccurrences: new Set(Object.keys(deckOccurrencesDefaults)),
  cardImage: new Set(Object.keys(cardImageDefaults)),
  divider: new Set(Object.keys(dividerDefaults)),
};

function block(type: CardBlockType, options?: CardBlockOptions): CardLayoutBlock {
  return options ? { id: newBlockId(), type, options } : { id: newBlockId(), type };
}

export const BUILT_IN_PRESETS: BuiltInPreset[] = [
  { name: 'Default', layout: buildLayoutFromLegacySettings(DEFAULT_CARD_DISPLAY_SETTINGS as StudySettingsDto) },
  {
    name: 'Minimal',
    layout: {
      version: 1,
      front: [block('headword')],
      back: [block('definitions'), block('exampleSentence')],
    },
  },
  {
    name: 'Sentence-first',
    layout: {
      version: 1,
      front: [block('exampleSentence', { blur: true }), block('headword')],
      back: [block('exampleSentence'), block('definitions')],
    },
  },
  {
    name: 'Listening',
    layout: {
      version: 1,
      front: [block('headword', { furigana: 'hidden' }), block('exampleSentence')],
      back: [block('definitions'), block('pitchAccent'), block('deckOccurrences')],
    },
  },
];

/** Deep-clones a layout, assigning every block a fresh id so applying a preset never shares ids. */
export function instantiatePreset(layout: CardLayout): CardLayout {
  const clone = (b: CardLayoutBlock): CardLayoutBlock =>
    b.options && Object.keys(b.options).length ? { id: newBlockId(), type: b.type, options: { ...b.options } } : { id: newBlockId(), type: b.type };
  return { version: 1, front: layout.front.map(clone), back: layout.back.map(clone) };
}

function toBase64Url(json: string): string {
  const bytes = new TextEncoder().encode(json);
  let bin = '';
  for (const byte of bytes) bin += String.fromCharCode(byte);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromBase64Url(code: string): string | null {
  try {
    const b64 = code.replace(/-/g, '+').replace(/_/g, '/');
    const bin = atob(b64);
    const bytes = Uint8Array.from(bin, (c) => c.charCodeAt(0));
    return new TextDecoder().decode(bytes);
  } catch {
    return null;
  }
}

// Only options carrying real values survive encoding; the importer refills defaults from the registry.
function compactOptions(options: CardBlockOptions | undefined): CardBlockOptions | undefined {
  if (!options) return undefined;
  const entries = Object.entries(options).filter(([, v]) => v !== undefined && v !== null);
  return entries.length ? (Object.fromEntries(entries) as CardBlockOptions) : undefined;
}

function encodeSide(blocks: CardLayoutBlock[]): { type: CardBlockType; options?: CardBlockOptions }[] {
  return blocks.map((b) => {
    const options = compactOptions(b.options);
    return options ? { type: b.type, options } : { type: b.type };
  });
}

export function encodeLayoutShareCode(layout: CardLayout): string {
  const payload = { version: 1, front: encodeSide(layout.front), back: encodeSide(layout.back) };
  return SHARE_CODE_PREFIX + toBase64Url(JSON.stringify(payload));
}

export interface DecodedShareLayout {
  layout: CardLayout;
  droppedTypes: string[];
}

function sanitiseOptions(type: CardBlockType, raw: unknown): CardBlockOptions | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  const allowed = OPTION_KEYS[type];
  if (!allowed) return undefined;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(raw as Record<string, unknown>)) {
    if (allowed.has(k) && (typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean')) out[k] = v;
  }
  return Object.keys(out).length ? (out as CardBlockOptions) : undefined;
}

function sanitiseSide(raw: unknown, dropped: Set<string>): CardLayoutBlock[] {
  if (!Array.isArray(raw)) return [];
  const out: CardLayoutBlock[] = [];
  for (const entry of raw) {
    if (out.length >= MAX_BLOCKS_PER_SIDE) break;
    if (!entry || typeof entry !== 'object') continue;
    const type = (entry as { type?: unknown }).type;
    if (typeof type !== 'string' || !VALID_TYPES.has(type)) {
      if (typeof type === 'string') dropped.add(type);
      continue;
    }
    const options = sanitiseOptions(type as CardBlockType, (entry as { options?: unknown }).options);
    out.push(block(type as CardBlockType, options));
  }
  return out;
}

/**
 * Parses a share code back into a layout. Returns null for a malformed prefix, base64, JSON or version.
 * Unknown block types are dropped (and reported in {@link DecodedShareLayout.droppedTypes}) so a code
 * produced by a future client still imports its recognised blocks.
 */
export function decodeLayoutShareCode(code: string): DecodedShareLayout | null {
  if (typeof code !== 'string') return null;
  const trimmed = code.trim();
  if (!trimmed.startsWith(SHARE_CODE_PREFIX)) return null;
  const json = fromBase64Url(trimmed.slice(SHARE_CODE_PREFIX.length));
  if (json === null) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== 'object') return null;
  if ((parsed as { version?: unknown }).version !== 1) return null;

  const dropped = new Set<string>();
  const layout: CardLayout = {
    version: 1,
    front: sanitiseSide((parsed as { front?: unknown }).front, dropped),
    back: sanitiseSide((parsed as { back?: unknown }).back, dropped),
  };
  return { layout, droppedTypes: [...dropped] };
}
