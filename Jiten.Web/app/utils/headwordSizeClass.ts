import type { TextSizeStep } from '~/utils/displayProfile';

// Literal class names, so Tailwind's source scan generates every step.
const BASE = ['text-base', 'text-lg', 'text-xl', 'text-2xl', 'text-3xl', 'text-4xl', 'text-5xl'];
const MD = ['md:text-base', 'md:text-lg', 'md:text-xl', 'md:text-2xl', 'md:text-3xl', 'md:text-4xl', 'md:text-5xl'];

/** Indexes into BASE / MD for [phone, md+] per length step. */
const STEPS: Array<[maxChars: number, base: [number, number], compact: [number, number]]> = [
  [8, [4, 4], [3, 3]],
  [14, [3, 4], [2, 3]],
  [Infinity, [2, 3], [1, 2]],
];

const SIZE_OFFSET: Record<TextSizeStep, number> = { sm: -1, md: 0, lg: 1, xl: 2 };

const clamp = (i: number) => Math.min(BASE.length - 1, Math.max(0, i));

/// Steps a headword's text size down by rendered length so a proverb fits in one line; short words keep the full size. The size setting shifts the whole ladder.
export function headwordSizeClass(text: string, compact = false, size: TextSizeStep = 'md'): string {
  const length = Array.from(text.replace(/\[[^\]]*\]/g, '')).length;
  const step = STEPS.find(([max]) => length <= max)!;
  const [phone, desktop] = (compact ? step[2] : step[1]).map((i) => clamp(i + SIZE_OFFSET[size]));
  return phone === desktop ? BASE[phone]! : `${BASE[phone]} ${MD[desktop]}`;
}

const SENTENCE_SIZE: Record<TextSizeStep, string> = {
  sm: 'text-xs md:text-base',
  md: 'text-sm md:text-lg',
  lg: 'text-base md:text-xl',
  xl: 'text-lg md:text-2xl',
};

export function sentenceSizeClass(size: TextSizeStep = 'md'): string {
  return SENTENCE_SIZE[size];
}
