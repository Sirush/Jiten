import { describe, expect, it } from 'vitest';
import {
  coverageToTail,
  tailToCoverage,
  coverageWindow,
  coverageTickDecimals,
  formatCoverageTick,
  tailWindow,
  tailTicks,
  formatTailCoverage,
} from '../app/utils/coverageAxis';

const HIGH = [97.9, 98.1, 98.4, 98.6, 98.9, 99.0, 99.2];
const LOW = [12, 15, 21, 28, 33, 40];

describe('coverageWindow', () => {
  it('zooms a high-coverage series so it fills the plot', () => {
    const w = coverageWindow(HIGH);
    expect(w.min).toBeLessThan(97.9);
    expect(w.min).toBeGreaterThan(97);
    expect(w.max).toBeGreaterThan(99.2);
    expect(w.max).toBeLessThanOrEqual(100);
  });

  it('pads a low-coverage series without going below zero', () => {
    const w = coverageWindow(LOW);
    expect(w.min).toBeGreaterThanOrEqual(0);
    expect(w.min).toBeLessThan(12);
    expect(w.max).toBeGreaterThan(40);
    expect(w.max).toBeLessThan(50);
  });

  it('keeps min strictly below max for a single point and a flat series', () => {
    for (const series of [[64.2], [99.2, 99.2, 99.2], [100, 100], [0, 0]]) {
      const w = coverageWindow(series);
      expect(w.min).toBeLessThan(w.max);
      expect(w.min).toBeGreaterThanOrEqual(0);
      expect(w.max).toBeLessThanOrEqual(100);
    }
  });

  it('falls back to the full axis for an empty series', () => {
    expect(coverageWindow([])).toEqual({ min: 0, max: 100 });
  });

  it('labels zoomed ticks with a decimal where the window needs one', () => {
    const high = coverageWindow(HIGH);
    expect(coverageTickDecimals(high)).toBe(1);
    expect(formatCoverageTick(98, coverageTickDecimals(high))).toBe('98.0%');
    expect(coverageTickDecimals(coverageWindow(LOW))).toBe(0);
    expect(formatCoverageTick(20, 0)).toBe('20%');
  });
});

describe('coverageToTail', () => {
  it('rises with coverage', () => {
    const u = HIGH.map(coverageToTail);
    for (let i = 1; i < u.length; i++) expect(u[i]!).toBeGreaterThan(u[i - 1]!);
    expect(coverageToTail(40)).toBeGreaterThan(coverageToTail(12));
  });

  it('stays finite at both ends', () => {
    expect(Number.isFinite(coverageToTail(100))).toBe(true);
    expect(coverageToTail(100)).toBeCloseTo(coverageToTail(99.99), 9);
    expect(Number.isFinite(coverageToTail(0))).toBe(true);
  });

  it('inverts back to the coverage percentage', () => {
    for (const c of [12, 50, 97.9, 99.2, 99.95]) expect(tailToCoverage(coverageToTail(c))).toBeCloseTo(c, 6);
  });
});

const MID = [95.44, 95.45, 95.5, 95.6, 95.62, 95.93, 95.95, 96.0];

describe('tailWindow and tailTicks', () => {
  it('shows decade-style ticks inside a high-coverage window', () => {
    const w = tailWindow(HIGH);
    const labels = tailTicks(w).map((t) => t.label);
    expect(labels).toContain('98%');
    expect(labels).toContain('99%');
    expect(w.min).toBeLessThan(coverageToTail(97.9));
    expect(w.max).toBeGreaterThan(coverageToTail(99.2));
  });

  it('uses round ticks for a low-coverage window', () => {
    const labels = tailTicks(tailWindow(LOW)).map((t) => t.label);
    expect(labels).toEqual(['10%', '20%', '30%', '40%']);
  });

  it('stays fitted to a mid-90s series instead of widening to the nearest decade anchors', () => {
    const w = tailWindow(MID);
    expect(tailToCoverage(w.min)).toBeGreaterThan(94);
    expect(tailToCoverage(w.max)).toBeLessThan(97.5);
    const labels = tailTicks(w).map((t) => t.label);
    expect(labels.length).toBeGreaterThanOrEqual(3);
    expect(labels).not.toContain('90%');
    for (const label of labels) {
      const value = parseFloat(label);
      expect(value).toBeGreaterThan(94);
      expect(value).toBeLessThan(97.5);
    }
  });

  it('always yields a usable ladder for narrow, single-point and edge series', () => {
    for (const series of [[99.55, 99.7, 99.85], [99.2], [100, 100], [0, 0], [99.991, 99.993]]) {
      const w = tailWindow(series);
      expect(w.min).toBeLessThan(w.max);
      const ticks = tailTicks(w);
      expect(ticks.length).toBeGreaterThanOrEqual(2);
      expect(ticks.length).toBeLessThanOrEqual(8);
      for (const t of ticks) {
        expect(t.value).toBeGreaterThanOrEqual(w.min - 1e-9);
        expect(t.value).toBeLessThanOrEqual(w.max + 1e-9);
      }
    }
  });

  it('keeps the fine ticks near 100 on a full-range series', () => {
    const labels = tailTicks(tailWindow([0.4, 3, 12, 40, 80, 95, 98.7])).map((t) => t.label);
    expect(labels).toContain('98%');
    expect(labels).toContain('95%');
    expect(labels.length).toBeLessThanOrEqual(8);
  });

  it('positions ticks ascending and labels them as coverage, never as remainders', () => {
    for (const series of [HIGH, MID, LOW]) {
      const ticks = tailTicks(tailWindow(series));
      for (let i = 1; i < ticks.length; i++) expect(ticks[i]!.value).toBeGreaterThan(ticks[i - 1]!.value);
      for (const t of ticks) expect(formatTailCoverage(tailToCoverage(t.value))).toBe(t.label);
    }
  });
});
