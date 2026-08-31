import { describe, expect, it } from 'vitest';
import { resolveDisplayedCardCount, type DisplayedCardCountInput } from '../app/utils/downloadCardCount';

const base: DisplayedCardCountInput = {
  mode: 'manual',
  isOccurrences: false,
  requiresAccurateCount: false,
  accurateCount: null,
  fallbackCount: 1234,
  wordCount: 8000,
};

describe('resolveDisplayedCardCount', () => {
  it('reports no count in Coverage % mode until the server answers', () => {
    expect(resolveDisplayedCardCount({ ...base, mode: 'target', requiresAccurateCount: true })).toBeNull();
  });

  it('never falls back to a locally derived number in Coverage % mode', () => {
    const result = resolveDisplayedCardCount({ ...base, mode: 'target', requiresAccurateCount: true, fallbackCount: 6400 });
    expect(result).not.toBe(6400);
  });

  it('uses the server count in Coverage % mode once it arrives', () => {
    expect(resolveDisplayedCardCount({ ...base, mode: 'target', requiresAccurateCount: true, accurateCount: 312 })).toBe(312);
  });

  it('shows zero rather than a placeholder when the server genuinely returns zero', () => {
    expect(resolveDisplayedCardCount({ ...base, mode: 'target', requiresAccurateCount: true, accurateCount: 0 })).toBe(0);
  });

  it('keeps the immediate fallback in manual mode while an exclusion count is in flight', () => {
    expect(resolveDisplayedCardCount({ ...base, requiresAccurateCount: true })).toBe(1234);
  });

  it('keeps the immediate fallback in occurrence mode while a count is in flight', () => {
    expect(resolveDisplayedCardCount({ ...base, mode: 'occurrence', requiresAccurateCount: true, fallbackCount: 77 })).toBe(77);
  });

  it('prefers the server count over the fallback outside Coverage % mode', () => {
    expect(resolveDisplayedCardCount({ ...base, requiresAccurateCount: true, accurateCount: 900 })).toBe(900);
  });

  it('reports the whole word count for occurrence-format downloads', () => {
    expect(resolveDisplayedCardCount({ ...base, mode: 'target', isOccurrences: true, requiresAccurateCount: true })).toBe(8000);
  });
});
