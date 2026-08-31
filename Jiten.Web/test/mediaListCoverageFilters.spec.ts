import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { clampRange, rangeChipLabel } from '../app/utils/rangeFilters';
import { MEDIA_RANGE_SPECS } from '../app/utils/mediaFilterRanges';

const MEDIA_LIST = readFileSync(fileURLToPath(new URL('../app/components/MediaList.vue', import.meta.url)), 'utf8');
const MEDIA_LIST_FILTERS = readFileSync(fileURLToPath(new URL('../app/components/MediaListFilters.vue', import.meta.url)), 'utf8');

const COVERAGE_PARAMS = ['totalCoverageMin', 'totalCoverageMax', 'uTotalCoverageMin', 'uTotalCoverageMax'] as const;

describe('coverage range normalization', () => {
  it('clamps a total coverage range to 0-100', () => {
    expect(clampRange(-20, 140, 0, 100)).toEqual({ min: 0, max: 100 });
  });

  it('pushes max up to min when the user drags min past it', () => {
    expect(clampRange(80, 40, 0, 100)).toEqual({ min: 80, max: 80 });
  });

  it('leaves an open-ended range open', () => {
    expect(clampRange(70, null, 0, 100)).toEqual({ min: 70, max: null });
    expect(clampRange(null, null, 0, 100)).toEqual({ min: null, max: null });
  });
});

describe('coverage filter chips', () => {
  const percent = (value: number) => `${value}%`;

  it('has no chip when neither bound is set', () => {
    expect(rangeChipLabel('Total coverage', null, null, percent)).toBeNull();
  });

  it('labels each bound shape', () => {
    expect(rangeChipLabel('Total coverage', 70, null, percent)).toBe('Total coverage 70% and up');
    expect(rangeChipLabel('Unique total coverage', null, 30, percent)).toBe('Unique total coverage up to 30%');
    expect(rangeChipLabel('Total coverage', 70, 90, percent)).toBe('Total coverage 70% - 90%');
    expect(rangeChipLabel('Total coverage', 70, 70, percent)).toBe('Total coverage 70%');
  });
});

// A missed wiring site produces a filter that half-works: it queries but never shows a chip, or
// survives "Reset all". Every site the existing mature pair occupies must carry the total pair too.
describe('total coverage filter wiring', () => {
  const occurrences = (source: string, name: string) => source.match(new RegExp(`\\b${name}\\b`, 'g'))?.length ?? 0;

  it.each([
    ['totalCoverageMin', 'coverageMin'],
    ['totalCoverageMax', 'coverageMax'],
    ['uTotalCoverageMin', 'uniqueCoverageMin'],
    ['uTotalCoverageMax', 'uniqueCoverageMax'],
  ])('wires %s as often as %s in MediaList.vue', (total, mature) => {
    expect(occurrences(MEDIA_LIST, total)).toBe(occurrences(MEDIA_LIST, mature));
  });

  it('exposes the four params as models on the filter panel', () => {
    for (const param of COVERAGE_PARAMS) {
      expect(MEDIA_LIST_FILTERS).toContain(`defineModel<number | null>('${param}'`);
    }
  });

  it('names mature and total explicitly on every coverage row', () => {
    const labels = MEDIA_RANGE_SPECS.map((spec) => spec.label);
    for (const label of ['Coverage (Mature)', 'Coverage (Total)', 'Unique Coverage (Mature)', 'Unique Coverage (Total)']) {
      expect(labels).toContain(label);
    }
  });
});
