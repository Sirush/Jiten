import type { ProfileSectionKey } from '~/types';

/** Keys match UserController.ProfilePalette; the server never stores raw colours. */
export const PROFILE_PALETTE = {
  slate: { avatar: '#334155', header: '#1e293b' },
  red: { avatar: '#b91c1c', header: '#991b1b' },
  orange: { avatar: '#c2410c', header: '#9a3412' },
  amber: { avatar: '#b45309', header: '#92400e' },
  green: { avatar: '#15803d', header: '#166534' },
  teal: { avatar: '#0f766e', header: '#115e59' },
  cyan: { avatar: '#0e7490', header: '#155e75' },
  blue: { avatar: '#1d4ed8', header: '#1e40af' },
  indigo: { avatar: '#4338ca', header: '#3730a3' },
  purple: { avatar: '#7e22ce', header: '#6b21a8' },
  pink: { avatar: '#be185d', header: '#9d174d' },
  rose: { avatar: '#be123c', header: '#9f1239' },
} as const;

export type ProfileColour = keyof typeof PROFILE_PALETTE;

export const PROFILE_COLOURS = Object.keys(PROFILE_PALETTE) as ProfileColour[];

const DEFAULT_HEADER_COLOUR: ProfileColour = 'indigo';

function isProfileColour(value: string | null | undefined): value is ProfileColour {
  return !!value && value in PROFILE_PALETTE;
}

/** Stable per name, so an avatar nobody customised keeps its colour across visits. */
function defaultAvatarColour(seed: string): ProfileColour {
  let hash = 0;
  for (const ch of seed) hash = (hash * 31 + ch.codePointAt(0)!) | 0;
  return PROFILE_COLOURS[Math.abs(hash) % PROFILE_COLOURS.length]!;
}

/** Keys match UserProfile.AvatarStyles. */
export type AvatarStyle = 'filled' | 'seal' | 'paper' | 'crest' | 'stroke' | 'brush';

export const AVATAR_STYLES: { value: AvatarStyle; label: string }[] = [
  { value: 'filled', label: 'Filled' },
  { value: 'seal', label: 'Seal' },
  { value: 'paper', label: 'Paper' },
  { value: 'crest', label: 'Crest' },
  { value: 'stroke', label: 'Stroke' },
  { value: 'brush', label: 'Brush' },
];

const AVATAR_FONT_LOADERS: Partial<Record<AvatarStyle, () => Promise<unknown>>> = {
  seal: () => import('~/assets/css/fonts/avatar-mincho.css'),
  crest: () => import('~/assets/css/fonts/avatar-mincho.css'),
  paper: () => import('~/assets/css/fonts/klee-one.css'),
  brush: () => import('~/assets/css/fonts/avatar-brush.css'),
};

/** Fetches the style's web font once an avatar uses it; the Japanese subsets are too large for the global stylesheet. */
export function loadAvatarFont(style: AvatarStyle) {
  return AVATAR_FONT_LOADERS[style]?.().catch(() => undefined);
}

export function avatarBackground(colour: string | null | undefined, seed: string): string {
  return PROFILE_PALETTE[isProfileColour(colour) ? colour : defaultAvatarColour(seed)].avatar;
}

/** Blends a #rrggbb colour toward white; amount 1 keeps the colour, 0 is white. */
export function tintColour(hex: string, amount: number): string {
  const value = parseInt(hex.slice(1), 16);
  return (
    '#' +
    [16, 8, 0]
      .map((shift) => Math.round(((value >> shift) & 255) * amount + 255 * (1 - amount)))
      .map((channel) => channel.toString(16).padStart(2, '0'))
      .join('')
  );
}

/** Without a chosen banner colour, the banner follows the avatar colour so profiles left at their defaults still differ. */
export function headerBackground(colour: string | null | undefined, avatarColour?: string | null, seed?: string): string {
  if (isProfileColour(colour)) return PROFILE_PALETTE[colour].header;
  if (isProfileColour(avatarColour)) return PROFILE_PALETTE[avatarColour].header;
  return PROFILE_PALETTE[seed ? defaultAvatarColour(seed) : DEFAULT_HEADER_COLOUR].header;
}

/** Mirrors UserController.AvatarCharacterRegex: one CJK ideograph (BMP or the U+20000 planes), hiragana or katakana. */
const AVATAR_CHARACTER = /^(?:[一-鿿]|[぀-ゟ]|[゠-ヿ]|[\u{20000}-\u{2FFFF}])$/u;

export function isAvatarCharacter(value: string): boolean {
  return AVATAR_CHARACTER.test(value);
}

export function avatarCharacter(kanji: string | null | undefined, name: string | null | undefined): string {
  if (kanji) return kanji;
  const first = name ? [...name.trim()][0] : undefined;
  return first ? first.toUpperCase() : '?';
}

/** Premade kanji avatars offered in the editor; any single kanji or kana can still be typed. */
export const PREMADE_AVATARS: { kanji: string; colour: ProfileColour }[] = [
  { kanji: '星', colour: 'purple' },
  { kanji: '月', colour: 'indigo' },
  { kanji: '空', colour: 'blue' },
  { kanji: '海', colour: 'cyan' },
  { kanji: '森', colour: 'green' },
  { kanji: '竹', colour: 'teal' },
  { kanji: '火', colour: 'red' },
  { kanji: '桜', colour: 'pink' },
  { kanji: '花', colour: 'rose' },
  { kanji: '秋', colour: 'orange' },
  { kanji: '金', colour: 'amber' },
  { kanji: '雪', colour: 'slate' },
  { kanji: '夢', colour: 'purple' },
  { kanji: '猫', colour: 'amber' },
  { kanji: '狐', colour: 'orange' },
  { kanji: '龍', colour: 'green' },
  { kanji: '本', colour: 'blue' },
  { kanji: '字', colour: 'slate' },
  { kanji: '雨', colour: 'cyan' },
  { kanji: '光', colour: 'amber' },
  { kanji: 'あ', colour: 'rose' },
  { kanji: 'ア', colour: 'teal' },
  { kanji: '日', colour: 'red' },
  { kanji: '心', colour: 'pink' },
];

export function profilePath(handle: string, tab?: ProfileTab): string {
  const base = `/profile/${encodeURIComponent(handle)}`;
  return tab && tab !== 'overview' ? `${base}/${tab}` : base;
}

export function profileApiPath(handle: string, sub?: string): string {
  const base = `user/profile/${encodeURIComponent(handle)}`;
  return sub ? `${base}/${sub}` : base;
}

/** Your own profile link: the display name once chosen; before that only the owner can open it, by login name. */
export function ownProfileHandle(user: { displayName?: string | null; userName?: string | null } | null | undefined): string | null {
  return user?.displayName || user?.userName || null;
}

export const PROFILE_TABS = [
  { key: 'overview', label: 'Overview', section: null },
  { key: 'library', label: 'Library', section: 'library' },
  { key: 'vocabulary', label: 'Vocabulary', section: 'vocabulary' },
  { key: 'stats', label: 'Stats', section: 'stats' },
  { key: 'lists', label: 'Lists', section: null },
] as const satisfies readonly { key: string; label: string; section: ProfileSectionKey | null }[];

export type ProfileTab = (typeof PROFILE_TABS)[number]['key'];
