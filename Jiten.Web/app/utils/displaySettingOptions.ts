import type { SentenceFuriganaMode } from '~/types';
import type { DifficultyPalette, HeadwordFuriganaMode, JapaneseFont, PitchAccentDisplay, ReducedMotionSetting, TextSizeStep } from '~/utils/displayProfile';

export const HEADWORD_FURIGANA_OPTIONS: { label: string; value: HeadwordFuriganaMode }[] = [
  { label: 'Always', value: 'shown' },
  { label: 'Words you don’t know', value: 'unknown' },
  { label: 'Never', value: 'hidden' },
];

export const SENTENCE_FURIGANA_OPTIONS: { label: string; value: SentenceFuriganaMode }[] = [
  { label: 'All words', value: 'all' },
  { label: 'All except the target word', value: 'exceptTarget' },
  { label: 'Words you don’t know', value: 'unknown' },
  { label: 'None', value: 'off' },
];

export const TEXT_SIZE_OPTIONS: { label: string; value: TextSizeStep }[] = [
  { label: 'Small', value: 'sm' },
  { label: 'Default', value: 'md' },
  { label: 'Large', value: 'lg' },
  { label: 'X-large', value: 'xl' },
];

/** `family` is the stack from main.css, so a tile draws its sample in the font it offers. */
export const JAPANESE_FONT_OPTIONS: { label: string; value: JapaneseFont; description: string; fontName: string; family: string }[] = [
  { label: 'Default', value: 'default', description: 'Standard gothic', fontName: 'Noto Sans JP', family: 'var(--font-noto-sans)' },
  {
    label: 'Textbook',
    value: 'kyokasho',
    description: 'Kanji as taught in schools',
    fontName: 'UD Digi Kyokasho or Klee One',
    family: 'var(--jiten-font-kyokasho)',
  },
  { label: 'Mincho', value: 'serif', description: 'Traditional print style', fontName: 'Yu Mincho or Hiragino Mincho', family: 'var(--jiten-font-serif)' },
  { label: 'Rounded', value: 'rounded', description: 'Soft, rounded strokes', fontName: 'Zen Maru Gothic', family: 'var(--jiten-font-rounded)' },
  { label: 'UD Gothic', value: 'ud', description: 'Designed for legibility', fontName: 'BIZ UDPGothic', family: 'var(--jiten-font-ud)' },
  { label: 'Installed font', value: 'custom', description: 'Choose an installed font', fontName: '', family: 'var(--jiten-font-custom-stack)' },
];

export const PITCH_ACCENT_OPTIONS: { label: string; value: PitchAccentDisplay }[] = [
  { label: 'Graph', value: 'graph' },
  { label: 'Number', value: 'number' },
  { label: 'Both', value: 'both' },
  { label: 'Hidden', value: 'hidden' },
];

export const DIFFICULTY_PALETTE_OPTIONS: { label: string; value: DifficultyPalette }[] = [
  { label: 'Standard', value: 'default' },
  { label: 'Red-green safe', value: 'redGreen' },
  { label: 'Blue-yellow safe', value: 'blueYellow' },
];

export const REDUCED_MOTION_OPTIONS: { label: string; value: ReducedMotionSetting }[] = [
  { label: 'Follow device', value: 'system' },
  { label: 'Always reduce', value: 'always' },
];

export const TITLE_LANGUAGE_OPTIONS = [
  { label: 'Japanese', value: 0 },
  { label: 'Romaji', value: 1 },
  { label: 'English', value: 2 },
];

export const TTS_VOICE_OPTIONS = [
  { label: 'Female 1', value: 'female' },
  { label: 'Female 2', value: 'female2' },
  { label: 'Male 1', value: 'male' },
  { label: 'Male 2', value: 'male2' },
  { label: 'ASMR', value: 'asmr' },
  { label: 'System', value: 'system' },
  { label: 'Random', value: 'random' },
];

export const DIFFICULTY_DISPLAY_STYLE_OPTIONS = [
  { label: 'Name only', value: 0 },
  { label: 'Name and value', value: 1 },
  { label: 'Value only', value: 2 },
];

export const DIFFICULTY_VALUE_DISPLAY_STYLE_OPTIONS = [
  { label: '0 to 5', value: 1 },
  { label: 'Percentage', value: 2 },
];

export const THEME_OPTIONS = [
  { label: 'Auto', value: 'auto', icon: 'material-symbols:brightness-auto-outline' },
  { label: 'Light', value: 'light', icon: 'material-symbols:light-mode-outline' },
  { label: 'Dark', value: 'dark', icon: 'material-symbols:dark-mode-outline' },
];

export const LIST_VIEW_OPTIONS = [
  { label: 'Cards', value: 0, icon: 'material-symbols:view-agenda-outline' },
  { label: 'Compact', value: 1, icon: 'material-symbols:grid-view' },
  { label: 'Table', value: 2, icon: 'material-symbols:table-rows' },
];
