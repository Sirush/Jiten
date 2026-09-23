import { describe, expect, it } from 'vitest';
import { isMasteredKanji, isTrackedKanji, kanjiGroupStats } from '../app/utils/kanjiGridStats';
import type { KanjiGridItem } from '../app/types';

function kanji(character: string, score: number): KanjiGridItem {
  return { character, score, wordCount: 0, frequencyRank: null, jlptLevel: null, grade: null, strokeCount: 0, readings: null };
}

describe('kanjiGroupStats', () => {
  it('counts seen and mastered against the full category', () => {
    const group = [kanji('一', 1), kanji('二', 0.5), kanji('三', 0), kanji('四', 0)];
    expect(kanjiGroupStats(group)).toEqual({ total: 4, seen: 2, mastered: 1 });
  });

  it('reports zero seen for a category with no tracked kanji', () => {
    expect(kanjiGroupStats([kanji('五', 0), kanji('六', 0)])).toEqual({ total: 2, seen: 0, mastered: 0 });
  });

  it('handles an empty category', () => {
    expect(kanjiGroupStats([])).toEqual({ total: 0, seen: 0, mastered: 0 });
  });
});

describe('kanji predicates', () => {
  it('treats a zero score as untracked', () => {
    expect(isTrackedKanji(kanji('七', 0))).toBe(false);
    expect(isTrackedKanji(kanji('七', 0.01))).toBe(true);
  });

  it('counts a mastered kanji as tracked', () => {
    const k = kanji('八', 0.9);
    expect(isMasteredKanji(k)).toBe(true);
    expect(isTrackedKanji(k)).toBe(true);
    expect(isMasteredKanji(kanji('九', 0.89))).toBe(false);
  });
});
