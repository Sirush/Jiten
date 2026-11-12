import { describe, expect, it } from 'vitest';
import { formatUnknownWordRatio } from '../app/utils/coverageRatio';

describe('formatUnknownWordRatio', () => {
  it.each([
    [50, '1 in 2 words unknown'],
    [80, '1 in 5 words unknown'],
    [85, '1 in 6.7 words unknown'],
    [90, '1 in 10 words unknown'],
    [97.3, '1 in 37 words unknown'],
    [99, '1 in 100 words unknown'],
    [99.3, '1 in 140 words unknown'],
    [99.5, '1 in 200 words unknown'],
    [99.95, '1 in 2,000 words unknown'],
  ])('formats %s%% coverage', (coverage, expected) => {
    expect(formatUnknownWordRatio(coverage)).toBe(expected);
  });

  it.each([0, 12, 49.9, 100, 100.4, Number.NaN])('has no ratio for %s%%', (coverage) => {
    expect(formatUnknownWordRatio(coverage)).toBeNull();
  });
});
