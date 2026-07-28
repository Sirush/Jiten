import { describe, expect, it } from 'vitest';
import { formatJourneyDelta, journeyWindow } from '../app/utils/journeyDelta';
import type { JourneyPoint } from '../app/types';

function monthlySeries(months: number, value: (i: number) => number): JourneyPoint[] {
  const points: JourneyPoint[] = [];
  for (let i = 0; i < months; i++) {
    const date = new Date(Date.UTC(2024, i, 1));
    points.push({
      date: date.toISOString().slice(0, 10),
      coverage: value(i),
      combinedCoverage: value(i),
      uniqueCoverage: value(i) / 2,
      combinedUniqueCoverage: value(i) / 2,
      knownWords: i,
      knownWordsCombined: i,
    });
  }
  return points;
}

const coverage = (p: JourneyPoint) => p.coverage;

describe('journeyWindow', () => {
  it('returns null below two points', () => {
    expect(journeyWindow([], coverage)).toBeNull();
    expect(
      journeyWindow(
        monthlySeries(1, () => 5),
        coverage
      )
    ).toBeNull();
  });

  it('anchors on a year back when the history is long enough', () => {
    const points = monthlySeries(36, (i) => i * 2);
    const window = journeyWindow(points, coverage)!;

    expect(window.sinceLabel).toBe('in the past year');
    expect(window.fromDate).toBe('2025-12-01');
    expect(window.currentValue).toBe(70);
    expect(window.delta).toBe(24);
  });

  it('falls back to the widest window the history covers', () => {
    expect(
      journeyWindow(
        monthlySeries(8, (i) => i),
        coverage
      )!.sinceLabel
    ).toBe('in the past 6 months');
    expect(
      journeyWindow(
        monthlySeries(4, (i) => i),
        coverage
      )!.sinceLabel
    ).toBe('in the past 3 months');
  });

  it('uses the whole history when it is shorter than a month', () => {
    const points: JourneyPoint[] = [
      { date: '2024-01-01', coverage: 1, combinedCoverage: 1, uniqueCoverage: 1, combinedUniqueCoverage: 1, knownWords: 1, knownWordsCombined: 1 },
      { date: '2024-01-08', coverage: 4, combinedCoverage: 4, uniqueCoverage: 4, combinedUniqueCoverage: 4, knownWords: 4, knownWordsCombined: 4 },
    ];
    const window = journeyWindow(points, coverage)!;

    expect(window.wholeHistory).toBe(true);
    expect(window.sinceLabel).toContain('since');
    expect(window.delta).toBe(3);
  });

  it('reports a decline and a flat line honestly', () => {
    const falling = journeyWindow(
      monthlySeries(14, (i) => 50 - i),
      coverage
    )!;
    expect(formatJourneyDelta(falling)).toBe('-12.0 pts in the past year');

    const flat = journeyWindow(
      monthlySeries(14, () => 50),
      coverage
    )!;
    expect(formatJourneyDelta(flat)).toBe('Unchanged in the past year');
  });
});
