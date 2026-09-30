import { isFontFamilyName, type JapaneseFont } from '~/utils/displayProfile';

const WEB_FONT_LOADERS: Partial<Record<JapaneseFont, () => Promise<unknown>>> = {
  kyokasho: () => import('~/assets/css/fonts/kyokasho.css'),
  rounded: () => import('~/assets/css/fonts/rounded.css'),
  ud: () => import('~/assets/css/fonts/ud.css'),
};

/** Fetches a font's web-font stylesheet once someone uses it; the Japanese subsets are too large for the global stylesheet. */
export function loadJapaneseFont(font: JapaneseFont) {
  return WEB_FONT_LOADERS[font]?.().catch(() => undefined);
}

/** The CSS value for an installed font's family name, or null when the name can't be used safely. */
export function customFontFamily(name: string | null | undefined): string | null {
  const trimmed = name?.trim();
  if (!trimmed || !isFontFamilyName(trimmed)) return null;
  return `"${trimmed}"`;
}

/** Compares glyph widths against two generic fallbacks: an uninstalled family falls back to both and matches each. */
export function isFontInstalled(name: string): boolean {
  const family = customFontFamily(name);
  if (!family || typeof document === 'undefined') return false;
  const context = document.createElement('canvas').getContext('2d');
  if (!context) return true;
  const sample = '言葉カタカナあいうABCabc012';
  return ['monospace', 'serif'].some((generic) => {
    context.font = `32px ${generic}`;
    const fallback = context.measureText(sample).width;
    context.font = `32px ${family}, ${generic}`;
    return context.measureText(sample).width !== fallback;
  });
}

const KANA_PROBE = 'あアの';
const PROBE_PX = 32;

/** Bundled web fonts rather than generics: the browser picks its system fallback per primary family, so only a declared fallback is predictable. */
const PROBE_FALLBACKS = ['"Klee One"', '"Zen Maru Gothic"'] as const;

/** A family with kana draws the same pixels whichever fallback follows it; one without falls through to two different fallbacks. */
export async function familiesWithJapanese(names: string[]): Promise<string[]> {
  if (typeof document === 'undefined') return names;
  await Promise.all([loadJapaneseFont('kyokasho'), loadJapaneseFont('rounded')]);
  await Promise.all(PROBE_FALLBACKS.map((family) => document.fonts.load(`${PROBE_PX}px ${family}`, KANA_PROBE).catch(() => [])));

  const canvas = document.createElement('canvas');
  canvas.width = PROBE_PX * KANA_PROBE.length;
  canvas.height = Math.ceil(PROBE_PX * 1.5);
  const context = canvas.getContext('2d', { willReadFrequently: true });
  if (!context) return names;

  const render = (fontFamily: string) => {
    context.clearRect(0, 0, canvas.width, canvas.height);
    context.font = `${PROBE_PX}px ${fontFamily}`;
    context.textBaseline = 'top';
    context.fillText(KANA_PROBE, 0, 0);
    return context.getImageData(0, 0, canvas.width, canvas.height).data;
  };
  const samePixels = (a: Uint8ClampedArray, b: Uint8ClampedArray) => a.every((value, i) => value === b[i]);

  const [first, second] = PROBE_FALLBACKS;
  if (samePixels(render(first), render(second))) return names;
  return names.filter((name) => {
    const family = customFontFamily(name);
    return !!family && samePixels(render(`${family}, ${first}`), render(`${family}, ${second}`));
  });
}

export const hasJapaneseGlyphs = async (name: string) => (await familiesWithJapanese([name])).length > 0;

interface LocalFontData {
  family: string;
}

export const canListLocalFonts = () => typeof window !== 'undefined' && 'queryLocalFonts' in window;

/** Japanese-capable family names from the Local Font Access API (Chromium desktop only); the browser asks the user for permission first. */
export async function listLocalFontFamilies(): Promise<string[]> {
  const fonts = await (window as unknown as { queryLocalFonts: () => Promise<LocalFontData[]> }).queryLocalFonts();
  const families = [...new Set(fonts.map((f) => f.family))].filter((family) => isFontFamilyName(family));
  return (await familiesWithJapanese(families)).sort((a, b) => a.localeCompare(b));
}
