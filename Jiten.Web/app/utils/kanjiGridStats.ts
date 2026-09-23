import type { KanjiGridItem } from '~/types';

export const KANJI_MASTERED_SCORE = 0.9;

export const isTrackedKanji = (k: KanjiGridItem): boolean => k.score > 0;

export const isMasteredKanji = (k: KanjiGridItem): boolean => k.score >= KANJI_MASTERED_SCORE;

export interface KanjiGroupStats {
  total: number;
  seen: number;
  mastered: number;
}

export function kanjiGroupStats(kanji: KanjiGridItem[]): KanjiGroupStats {
  let seen = 0;
  let mastered = 0;
  for (const k of kanji) {
    if (isTrackedKanji(k)) seen++;
    if (isMasteredKanji(k)) mastered++;
  }
  return { total: kanji.length, seen, mastered };
}
