const STEPS: Array<[maxChars: number, base: string, compact: string]> = [
  [8, 'text-3xl', 'text-2xl'],
  [14, 'text-2xl md:text-3xl', 'text-xl md:text-2xl'],
  [Infinity, 'text-xl md:text-2xl', 'text-lg md:text-xl'],
];

/// Steps a headword's text size down by rendered length so a proverb fits in one line; short words keep the full size.
export function headwordSizeClass(text: string, compact = false): string {
  const length = Array.from(text.replace(/\[[^\]]*\]/g, '')).length;
  const step = STEPS.find(([max]) => length <= max)!;
  return compact ? step[2] : step[1];
}
