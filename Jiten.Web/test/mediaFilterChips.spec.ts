import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import {
  buildRangeChips,
  countActiveFilters,
  MEDIA_RANGE_SPECS,
  sliderBounds,
  type MediaFilterSnapshot,
  type MediaRangeKey,
} from '../app/utils/mediaFilterRanges';
import type { RangeBounds } from '../app/utils/rangeFilters';

const CHIPS = readFileSync(fileURLToPath(new URL('../app/components/MediaListFilterChips.vue', import.meta.url)), 'utf8');

const emptyRanges = () =>
  Object.fromEntries(MEDIA_RANGE_SPECS.map((spec) => [spec.key, { min: null, max: null }])) as Record<MediaRangeKey, RangeBounds>;

const snapshot = (overrides: Partial<MediaFilterSnapshot> = {}): MediaFilterSnapshot => ({
  ranges: emptyRanges(),
  statusFilter: 'none',
  includeGenres: [],
  excludeGenres: [],
  includeTags: [],
  excludeTags: [],
  excludeSequels: null,
  favourite: null,
  ...overrides,
});

const withRange = (key: MediaRangeKey, bounds: RangeBounds) => {
  const ranges = emptyRanges();
  ranges[key] = bounds;
  return ranges;
};

describe('active filter count', () => {
  it('counts nothing on a clean panel', () => {
    expect(countActiveFilters(snapshot())).toBe(0);
  });

  it('counts a range once whether one bound is set or both', () => {
    expect(countActiveFilters(snapshot({ ranges: withRange('difficulty', { min: 2, max: null }) }))).toBe(1);
    expect(countActiveFilters(snapshot({ ranges: withRange('difficulty', { min: null, max: 4 }) }))).toBe(1);
    expect(countActiveFilters(snapshot({ ranges: withRange('difficulty', { min: 2, max: 4 }) }))).toBe(1);
  });

  it('counts status, favourite, each genre, each tag and the sequel exclusion separately', () => {
    const count = countActiveFilters(
      snapshot({
        ranges: withRange('charCount', { min: null, max: 500_000 }),
        statusFilter: 'completed',
        includeGenres: [7],
        excludeGenres: [5],
        includeTags: [1, 2],
        excludeSequels: true,
        favourite: true,
      })
    );
    expect(count).toBe(8);
  });
});

describe('slider bounds', () => {
  const difficulty = MEDIA_RANGE_SPECS.find((spec) => spec.key === 'difficulty')!;

  it('drops a bound parked on the floor or the ceiling', () => {
    expect(sliderBounds(difficulty, 0, 5)).toEqual({ min: null, max: null });
    expect(sliderBounds(difficulty, 2, 5)).toEqual({ min: 2, max: null });
    expect(sliderBounds(difficulty, 0, 4)).toEqual({ min: null, max: 4 });
  });

  it('adds no phantom filter for a range dragged back to its full span', () => {
    const bounds = sliderBounds(difficulty, difficulty.floor, difficulty.ceil);
    expect(countActiveFilters(snapshot({ ranges: withRange('difficulty', bounds) }))).toBe(0);
  });
});

describe('applied filter chips', () => {
  it('renders one chip per applied range, worded like the panel', () => {
    const ranges = emptyRanges();
    ranges.difficulty = { min: 2, max: 4 };
    ranges.charCount = { min: null, max: 500_000 };
    ranges.totalCoverage = { min: 80, max: null };

    expect(buildRangeChips(ranges)).toEqual([
      { key: 'charCount', label: 'Characters up to 500,000' },
      { key: 'difficulty', label: 'Difficulty 2.0 - 4.0' },
      { key: 'totalCoverage', label: 'Coverage (Total) 80% and up' },
    ]);
  });

  it('renders no chip for an untouched range', () => {
    expect(buildRangeChips(emptyRanges())).toEqual([]);
  });

  it('gives every chip a way to clear itself and offers a clear-all', () => {
    expect(CHIPS).toContain('Remove filter');
    expect(CHIPS).toContain('Clear all');
    for (const source of ['includeGenres', 'excludeGenres', 'includeTags', 'excludeTags', 'excludeSequels', 'statusFilter']) {
      expect(CHIPS).toContain(source);
    }
  });
});
