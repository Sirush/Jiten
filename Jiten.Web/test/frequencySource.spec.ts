import { describe, expect, it } from 'vitest';
import type { ResolvedFrequencyRank } from '../app/types/types';
import { frequencySourcePatch, frequencySourceValue, rowRankLabel } from '../app/utils/frequencySource';

describe('frequencySourceValue', () => {
  it('reads global as 0', () => {
    expect(frequencySourceValue({ source: 'global', rank: 14300, isFallback: false })).toBe(0);
    expect(frequencySourceValue(null)).toBe(0);
  });

  it('reads a media type as its id', () => {
    expect(frequencySourceValue({ source: 'mediaType', mediaType: 1, rank: 520, isFallback: false })).toBe(1);
  });

  it('keeps the chosen media type when the rank fell back to global', () => {
    const fallback: ResolvedFrequencyRank = { source: 'global', mediaType: 1, rank: 14300, isFallback: true };
    expect(frequencySourceValue(fallback)).toBe(1);
  });

  it('reads a custom list as a negative id', () => {
    expect(frequencySourceValue({ source: 'list', listId: 7, rank: 3, isFallback: false })).toBe(-7);
  });
});

describe('frequencySourcePatch', () => {
  it('sends explicit zeroes for global so the server clears the default', () => {
    expect(frequencySourcePatch(0)).toEqual({ defaultFrequencyMediaType: 0, defaultFrequencyListId: 0 });
  });

  it('sets one source and clears the other', () => {
    expect(frequencySourcePatch(4)).toEqual({ defaultFrequencyMediaType: 4, defaultFrequencyListId: 0 });
    expect(frequencySourcePatch(-7)).toEqual({ defaultFrequencyMediaType: 0, defaultFrequencyListId: 7 });
  });

  it('round-trips a resolved source', () => {
    const resolved: ResolvedFrequencyRank = { source: 'list', listId: 12, rank: 0, isFallback: false };
    expect(frequencySourcePatch(frequencySourceValue(resolved)).defaultFrequencyListId).toBe(12);
  });
});

describe('rowRankLabel', () => {
  it('labels a media-type hit with the requested source', () => {
    const row = rowRankLabel({ frequencyRank: 1204, frequencyRankSource: 'mediaType', isFrequencyFallback: false }, 'Anime');
    expect(row).toEqual({ rank: '1,204', source: 'Anime', hint: null });
  });

  it('relabels a fallback rank as global and explains it', () => {
    const row = rowRankLabel({ frequencyRank: 330850, frequencyRankSource: 'global', isFrequencyFallback: true }, 'Anime');
    expect(row.rank).toBe('330,850');
    expect(row.source).toBe('global');
    expect(row.hint).toBe('Not present in Anime yet, so this is the global rank instead.');
  });

  it('prints a dash for a word outside a custom list, keeping the list label', () => {
    const row = rowRankLabel({ frequencyRank: 0, frequencyRankSource: 'list', isFrequencyFallback: false }, 'My list');
    expect(row).toEqual({ rank: '—', source: 'My list', hint: null });
  });

  it('shows no source line on the site-wide ranking', () => {
    expect(rowRankLabel({ frequencyRank: 85360 })).toEqual({ rank: '85,360', source: null, hint: null });
    expect(rowRankLabel({ frequencyRank: 85360, frequencyRankSource: 'global', isFrequencyFallback: false }, 'Anime')).toEqual({
      rank: '85,360',
      source: null,
      hint: null,
    });
  });
});
