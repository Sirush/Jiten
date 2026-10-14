import { describe, expect, it } from 'vitest';
import { MediaType } from '../app/types/enums';
import {
  bandPosition,
  describeShare,
  floorToHundred,
  formatCompact,
  formatMonth,
  heatStep,
  isWatchedType,
  medianShareOfTopWords,
  rankLabel,
  roundToHundred,
  watchedFigure,
} from '../app/utils/siteStats';

const BANDS = [
  { from: 1, to: 100, p25: 38, median: 62, p75: 96, p90: 96 },
  { from: 101, to: 250, p25: 28, median: 68, p75: 89.3, p90: 89.3 },
  { from: 251, to: 500, p25: 16.8, median: 69.2, p75: 87.6, p90: 87.6 },
  { from: 501, to: 1000, p25: 7.2, median: 66.6, p75: 86, p90: 86 },
  { from: 1001, to: 2000, p25: 2.7, median: 40.3, p75: 73.6, p90: 73.6 },
];

describe('number formatting', () => {
  it('keeps full values under ten thousand and compacts above', () => {
    expect(formatCompact(2899)).toBe('2,899');
    expect(formatCompact(22_317_818)).toBe('22.3M');
    expect(formatCompact(2_610_573_012)).toBe('2.6B');
  });

  it('rounds and floors to the hundred', () => {
    expect(roundToHundred(3069)).toBe(3100);
    expect(roundToHundred(3049)).toBe(3000);
    expect(floorToHundred(5756)).toBe(5700);
  });

  it('labels ranks in thousands from 1,000', () => {
    expect(rankLabel(250)).toBe('250');
    expect(rankLabel(7500)).toBe('7.5k');
  });
});

describe('frequency bands', () => {
  it('places a band at its geometric middle', () => {
    expect(bandPosition({ from: 1, to: 100 })).toBe(10);
    expect(bandPosition({ from: 10001, to: 15000 })).toBeCloseTo(12247.8, 0);
  });

  it('weights the band medians by band size up to the limit', () => {
    const share = medianShareOfTopWords(BANDS, 1000)!;
    expect(share).toBeCloseTo((62 * 100 + 68 * 150 + 69.2 * 250 + 66.6 * 500) / 1000, 5);
  });

  it('returns null when no band ends inside the limit', () => {
    expect(medianShareOfTopWords(BANDS, 50)).toBeNull();
  });

  it('names common fractions and falls back to a percentage', () => {
    expect(describeShare(66.6)).toBe('two thirds');
    expect(describeShare(51)).toBe('half');
    expect(describeShare(58)).toBe('58%');
  });
});

describe('read and watch rows', () => {
  const row = (mediaType: MediaType | null, titlesFinished: number, unitsFinished: number) => ({
    mediaType,
    learners: 50,
    characters: 1000,
    titlesFinished,
    unitsFinished,
  });

  it('splits watched types from read types, with Other read', () => {
    expect(isWatchedType(MediaType.Anime)).toBe(true);
    expect(isWatchedType(MediaType.YouTube)).toBe(true);
    expect(isWatchedType(MediaType.VisualNovel)).toBe(false);
    expect(isWatchedType(null)).toBe(false);
  });

  it('counts films for movies and episodes or videos for series', () => {
    expect(watchedFigure(row(MediaType.Movie, 192, 193))).toEqual({ value: 192, unit: 'films' });
    expect(watchedFigure(row(MediaType.Anime, 9494, 134160))).toEqual({ value: 134160, unit: 'episodes' });
    expect(watchedFigure(row(MediaType.YouTube, 2, 1))).toEqual({ value: 1, unit: 'video' });
  });
});

describe('dates and heatmap', () => {
  it('formats months in UTC', () => {
    expect(formatMonth('2026-01-01', true)).toBe('Jan 2026');
    expect(formatMonth('2025-09-01', false)).toBe('Sep');
  });

  it('maps cells to five steps relative to the busiest', () => {
    expect(heatStep(0, 100)).toBe(0);
    expect(heatStep(1, 100)).toBe(1);
    expect(heatStep(50, 100)).toBe(3);
    expect(heatStep(100, 100)).toBe(5);
  });
});
