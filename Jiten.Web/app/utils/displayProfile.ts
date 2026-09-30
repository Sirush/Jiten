import { DifficultyDisplayStyle, DifficultyValueDisplayStyle, DisplayStyle, ThemeMode, TitleLanguage } from '~/types';
import type { SentenceFuriganaMode } from '~/types';
import type { KanjiScalePref } from '~/data/kanjiGroupings';
import { DEFAULT_WORD_STATE_COLOURS, WORD_STATE_COLOUR_KEYS, type WordStateColours } from '~/utils/wordState';
import { sanitiseMediaCardStatColumns, type MediaCardStatColumns } from '~/utils/mediaCardStats';
import { DEFAULT_MEDIA_CARD_SECTION_LAYOUT, copySectionLayout, isMediaCardSectionLayout, type MediaCardSectionLayout } from '~/utils/mediaCardSections';

export type HeadwordFuriganaMode = 'shown' | 'unknown' | 'hidden';
export type TextSizeStep = 'sm' | 'md' | 'lg' | 'xl';
export type JapaneseFont = 'default' | 'kyokasho' | 'serif' | 'rounded' | 'ud' | 'custom';
export type PitchAccentDisplay = 'graph' | 'number' | 'both' | 'hidden';
export type DifficultyPalette = 'default' | 'redGreen' | 'blueYellow';
export type ReducedMotionSetting = 'system' | 'always';
export type TtsVoice = 'female' | 'female2' | 'male' | 'male2' | 'asmr' | 'system' | 'random';

export interface DisplayValues {
  titleLanguage: TitleLanguage;
  themeMode: ThemeMode;
  headwordFurigana: HeadwordFuriganaMode;
  sentenceFurigana: SentenceFuriganaMode;
  headwordSize: TextSizeStep;
  sentenceSize: TextSizeStep;
  japaneseFont: JapaneseFont;
  /** An installed font's family name, used when japaneseFont is 'custom'. */
  japaneseCustomFont: string;
  /** Titles and descriptions keep the default font; words, readings and sentences still use japaneseFont. */
  japaneseFontWordsOnly: boolean;
  /** Custom dictionary definitions keep the default font unless this is on. */
  japaneseFontDictionaries: boolean;
  furiganaSize: TextSizeStep;
  /** Furigana the other settings hide still appears while the pointer is over its word. */
  furiganaOnHover: boolean;
  pitchAccentDisplay: PitchAccentDisplay;
  pitchAccentColours: boolean;
  colourWordsByState: boolean;
  stateColours: WordStateColours;
  reducedMotion: ReducedMotionSetting;
  readingSpeed: number;
  readingSpeedByDifficulty: boolean;
  /** Characters per hour for each difficulty band, Beginner to Insane; used instead of readingSpeed when readingSpeedByDifficulty is on. */
  readingSpeeds: number[];
  displayAllNsfw: boolean;
  hideVocabularyDefinitions: boolean;
  hideCoverageBorders: boolean;
  hideGenres: boolean;
  hideTags: boolean;
  hideRelations: boolean;
  hideDescriptions: boolean;
  hideExternalRating: boolean;
  hideAlternativeTitles: boolean;
  mediaCardSectionLayout: MediaCardSectionLayout;
  quickMasterVocabulary: boolean;
  ttsVoice: TtsVoice;
  difficultyDisplayStyle: DifficultyDisplayStyle;
  difficultyValueDisplayStyle: DifficultyValueDisplayStyle;
  difficultyPalette: DifficultyPalette;
  kanjiScale: KanjiScalePref;
  listView: DisplayStyle;
}

export type DisplayValueKey = keyof DisplayValues;

export interface DisplayProfile {
  id: string;
  name: string;
  values: Partial<DisplayValues>;
  statColumns: MediaCardStatColumns | null;
  updatedAt: number;
}

export const MIN_READING_SPEED = 100;
export const MAX_READING_SPEED = 100_000;
export const DEFAULT_READING_SPEEDS = [20000, 17000, 14000, 11000, 9000, 7000];

export const DEFAULT_DISPLAY_VALUES: DisplayValues = {
  titleLanguage: TitleLanguage.Romaji,
  themeMode: ThemeMode.Auto,
  headwordFurigana: 'shown',
  sentenceFurigana: 'all',
  headwordSize: 'md',
  sentenceSize: 'md',
  japaneseFont: 'default',
  japaneseCustomFont: '',
  japaneseFontWordsOnly: false,
  japaneseFontDictionaries: false,
  furiganaSize: 'md',
  furiganaOnHover: false,
  pitchAccentDisplay: 'graph',
  pitchAccentColours: false,
  colourWordsByState: false,
  stateColours: { ...DEFAULT_WORD_STATE_COLOURS },
  reducedMotion: 'system',
  readingSpeed: 14000,
  readingSpeedByDifficulty: false,
  readingSpeeds: [...DEFAULT_READING_SPEEDS],
  displayAllNsfw: false,
  hideVocabularyDefinitions: false,
  hideCoverageBorders: false,
  hideGenres: false,
  hideTags: false,
  hideRelations: false,
  hideDescriptions: false,
  hideExternalRating: false,
  hideAlternativeTitles: false,
  mediaCardSectionLayout: copySectionLayout(DEFAULT_MEDIA_CARD_SECTION_LAYOUT),
  quickMasterVocabulary: false,
  ttsVoice: 'female',
  difficultyDisplayStyle: DifficultyDisplayStyle.Name,
  difficultyValueDisplayStyle: DifficultyValueDisplayStyle.ZeroToFive,
  difficultyPalette: 'default',
  kanjiScale: 'jlpt',
  listView: DisplayStyle.Card,
};

export const MAX_DISPLAY_PROFILES = 10;
export const MAX_DISPLAY_PROFILE_NAME = 40;

const oneOf =
  <T>(...allowed: T[]) =>
  (v: unknown) =>
    allowed.includes(v as T);
const isBool = (v: unknown) => typeof v === 'boolean';
/** Rendered inside a quoted CSS string, so quotes, backslashes and semicolons must never pass. */
export const isFontFamilyName = (v: unknown) => typeof v === 'string' && /^[\p{L}\p{N} _.-]{0,80}$/u.test(v);
const isHex = (v: unknown) => typeof v === 'string' && /^#[0-9a-fA-F]{6}$/.test(v);
const isReadingSpeed = (v: unknown) => Number.isInteger(v) && (v as number) >= MIN_READING_SPEED && (v as number) <= MAX_READING_SPEED;

/** Mirrors DisplayProfileSanitizer.ValueRules on the API; both sides must gain a key together. */
export const DISPLAY_VALUE_SCHEMA: Record<DisplayValueKey, (v: unknown) => boolean> = {
  titleLanguage: oneOf(0, 1, 2),
  themeMode: oneOf('light', 'dark', 'auto'),
  headwordFurigana: oneOf('shown', 'unknown', 'hidden'),
  sentenceFurigana: oneOf('all', 'exceptTarget', 'unknown', 'off'),
  headwordSize: oneOf('sm', 'md', 'lg', 'xl'),
  sentenceSize: oneOf('sm', 'md', 'lg', 'xl'),
  japaneseFont: oneOf('default', 'kyokasho', 'serif', 'rounded', 'ud', 'custom'),
  japaneseCustomFont: isFontFamilyName,
  japaneseFontWordsOnly: isBool,
  japaneseFontDictionaries: isBool,
  furiganaSize: oneOf('sm', 'md', 'lg', 'xl'),
  furiganaOnHover: isBool,
  pitchAccentDisplay: oneOf('graph', 'number', 'both', 'hidden'),
  pitchAccentColours: isBool,
  colourWordsByState: isBool,
  stateColours: (v) =>
    typeof v === 'object' &&
    v !== null &&
    !Array.isArray(v) &&
    Object.keys(v).length === WORD_STATE_COLOUR_KEYS.length &&
    WORD_STATE_COLOUR_KEYS.every((k) => k in v && ((v as Record<string, unknown>)[k] === null || isHex((v as Record<string, unknown>)[k]))),
  reducedMotion: oneOf('system', 'always'),
  readingSpeed: isReadingSpeed,
  readingSpeedByDifficulty: isBool,
  readingSpeeds: (v) => Array.isArray(v) && v.length === DEFAULT_READING_SPEEDS.length && v.every(isReadingSpeed),
  displayAllNsfw: isBool,
  hideVocabularyDefinitions: isBool,
  hideCoverageBorders: isBool,
  hideGenres: isBool,
  hideTags: isBool,
  hideRelations: isBool,
  hideDescriptions: isBool,
  hideExternalRating: isBool,
  hideAlternativeTitles: isBool,
  mediaCardSectionLayout: isMediaCardSectionLayout,
  quickMasterVocabulary: isBool,
  ttsVoice: oneOf('female', 'female2', 'male', 'male2', 'asmr', 'system', 'random'),
  difficultyDisplayStyle: oneOf(0, 1, 2),
  difficultyValueDisplayStyle: oneOf(1, 2),
  difficultyPalette: oneOf('default', 'redGreen', 'blueYellow'),
  kanjiScale: oneOf('jlpt', 'grade', 'kanken', 'wanikani', 'rtk', 'klc', 'tmw', 'none'),
  listView: oneOf(0, 1, 2),
};

export const DISPLAY_VALUE_KEYS = Object.keys(DISPLAY_VALUE_SCHEMA) as DisplayValueKey[];

/** The Display page section each key is edited in, for per-section resets; every key belongs to exactly one. */
export const DISPLAY_SECTION_KEYS = {
  japaneseText: [
    'headwordFurigana',
    'sentenceFurigana',
    'furiganaSize',
    'furiganaOnHover',
    'headwordSize',
    'sentenceSize',
    'japaneseFont',
    'japaneseCustomFont',
    'japaneseFontWordsOnly',
    'japaneseFontDictionaries',
    'pitchAccentDisplay',
    'pitchAccentColours',
  ],
  wordColours: ['colourWordsByState', 'stateColours'],
  appearance: ['themeMode', 'titleLanguage', 'listView', 'reducedMotion'],
  mediaPages: ['difficultyDisplayStyle', 'difficultyValueDisplayStyle', 'difficultyPalette', 'readingSpeed', 'readingSpeedByDifficulty', 'readingSpeeds'],
  mediaCards: [
    'hideCoverageBorders',
    'hideGenres',
    'hideTags',
    'hideRelations',
    'hideDescriptions',
    'hideExternalRating',
    'hideAlternativeTitles',
    'mediaCardSectionLayout',
  ],
  vocabulary: ['hideVocabularyDefinitions', 'quickMasterVocabulary', 'displayAllNsfw', 'kanjiScale', 'ttsVoice'],
} as const satisfies Record<string, readonly DisplayValueKey[]>;

export type DisplaySectionId = keyof typeof DISPLAY_SECTION_KEYS;

export function sanitiseDisplayValues(raw: unknown): Partial<DisplayValues> {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return {};
  const result: Partial<DisplayValues> = {};
  for (const key of DISPLAY_VALUE_KEYS) {
    const value = (raw as Record<string, unknown>)[key];
    if (DISPLAY_VALUE_SCHEMA[key](value)) (result as Record<string, unknown>)[key] = value;
  }
  return result;
}

/** Order-insensitive comparison, so a profile saved by another client with a different key order still matches. */
export function displayValuesEqual(a: Partial<DisplayValues>, b: Partial<DisplayValues>): boolean {
  return DISPLAY_VALUE_KEYS.every((key) => JSON.stringify(a[key] ?? null) === JSON.stringify(b[key] ?? null));
}

export function statColumnsEqual(a: MediaCardStatColumns | null, b: MediaCardStatColumns | null): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

export function newDisplayProfileId(): string {
  const bytes = new Uint8Array(9);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => 'abcdefghijklmnopqrstuvwxyz0123456789'[b % 36]).join('');
}

export function trimProfileName(name: string): string {
  return name.trim().slice(0, MAX_DISPLAY_PROFILE_NAME);
}

const EXPORT_FORMAT = 'jiten-display-profile';

export function exportDisplayProfile(name: string, values: Partial<DisplayValues>, statColumns: MediaCardStatColumns | null): string {
  return JSON.stringify({ format: EXPORT_FORMAT, version: 2, name, values, statColumns }, null, 2);
}

/** Unknown keys and bad values are dropped, not rejected, so a file from a newer or hand-edited export still imports. */
export function parseDisplayProfileImport(text: string): { name: string; values: Partial<DisplayValues>; statColumns: MediaCardStatColumns | null } | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null || (parsed as { format?: unknown }).format !== EXPORT_FORMAT) return null;

  const file = parsed as { name?: unknown; values?: unknown; statColumns?: unknown };
  const name = typeof file.name === 'string' && file.name.trim() ? trimProfileName(file.name) : 'Imported profile';
  return { name, values: sanitiseDisplayValues(file.values), statColumns: sanitiseMediaCardStatColumns(file.statColumns) };
}

/** The device's remembered profile, or the first one when it was deleted or never chosen. */
export function pickActiveProfile(profiles: DisplayProfile[], activeId: string | null | undefined): DisplayProfile | null {
  return profiles.find((p) => p.id === activeId) ?? profiles[0] ?? null;
}

/** Compared against the profile as it renders, so a key the profile lacks counts as its default. */
export function localSettingsDiffer(local: { values: DisplayValues; statColumns: MediaCardStatColumns | null }, profile: DisplayProfile): boolean {
  return !displayValuesEqual(local.values, { ...DEFAULT_DISPLAY_VALUES, ...profile.values }) || !statColumnsEqual(local.statColumns, profile.statColumns);
}

export function localSettingsWorthKeeping(local: { values: DisplayValues; statColumns: MediaCardStatColumns | null }, profile: DisplayProfile): boolean {
  const untouched = displayValuesEqual(local.values, DEFAULT_DISPLAY_VALUES) && local.statColumns === null;
  return !untouched && localSettingsDiffer(local, profile);
}
