import { describe, expect, it } from 'vitest';
import { isMostlyJapanese, japaneseTextAttrs } from '../app/utils/japaneseTextAttrs';

describe('isMostlyJapanese', () => {
  it('accepts Japanese titles, including ones mixing in Latin', () => {
    expect(isMostlyJapanese('ゆるキャン△')).toBe(true);
    expect(isMostlyJapanese('Re:ゼロから始める異世界生活')).toBe(true);
  });

  it('rejects romaji, English, and English quoting a Japanese name', () => {
    expect(isMostlyJapanese('Yuru Camp')).toBe(false);
    expect(isMostlyJapanese('A story about Taki (瀧) and Mitsuha (三葉), two teenagers who swap bodies.')).toBe(false);
    expect(isMostlyJapanese('')).toBe(false);
    expect(isMostlyJapanese(null)).toBe(false);
  });
});

describe('japaneseTextAttrs', () => {
  it('marks Japanese text so the words-only switch can find it', () => {
    expect(japaneseTextAttrs('君の名は。')).toEqual({ lang: 'ja', class: 'ja-general' });
    expect(japaneseTextAttrs('Your Name')).toEqual({});
  });
});
