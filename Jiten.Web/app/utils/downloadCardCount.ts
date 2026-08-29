export type DownloadCountMode = 'manual' | 'target' | 'occurrence';

export interface DisplayedCardCountInput {
  mode: DownloadCountMode;
  isOccurrences: boolean;
  requiresAccurateCount: boolean;
  accurateCount: number | null;
  fallbackCount: number;
  wordCount: number;
}

// Null means the count is not known yet
export function resolveDisplayedCardCount(input: DisplayedCardCountInput): number | null {
  if (input.isOccurrences) return input.wordCount;
  if (input.mode === 'target') return input.accurateCount;
  if (input.requiresAccurateCount) return input.accurateCount ?? input.fallbackCount;
  return input.fallbackCount;
}
