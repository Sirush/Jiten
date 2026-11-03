import { describe, expect, it } from 'vitest';
import { rollingMedian, trendWindow } from '../app/utils/rollingMedian';

describe('trendWindow', () => {
  it('is odd and at least five', () => {
    expect(trendWindow(6)).toBe(5);
    expect(trendWindow(48)).toBe(7);
    expect(trendWindow(80)).toBe(11);
  });
});

describe('rollingMedian', () => {
  it('ignores a single outlier', () => {
    expect(rollingMedian([2, 2, 2, 5, 2, 2, 2], 5)).toEqual([2, 2, 2, 2, 2, 2, 2]);
  });

  it('keeps the window full at the ends', () => {
    expect(rollingMedian([1, 2, 3, 4, 5, 6, 7], 5)).toEqual([3, 3, 3, 4, 5, 5, 5]);
  });

  it('follows a step up', () => {
    expect(rollingMedian([1, 1, 1, 1, 3, 3, 3, 3], 3)).toEqual([1, 1, 1, 1, 3, 3, 3, 3]);
  });

  it('shrinks the window to a short series', () => {
    expect(rollingMedian([1, 3], 5)).toEqual([2, 2]);
  });
});
