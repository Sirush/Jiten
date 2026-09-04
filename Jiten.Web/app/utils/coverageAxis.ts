export interface AxisWindow {
  min: number;
  max: number;
}

export interface AxisTick {
  value: number;
  label: string;
}

export type CoverageScale = 'fit' | 'log' | 'full';

export const TAIL_FLOOR = 0.01;

const LINEAR_PAD = 0.1;
const LINEAR_MIN_SPAN = 1;
const TAIL_PAD = 0.1;
const TAIL_MIN_SPAN = 0.1;
const TAIL_TARGET_TICKS = 5;
const TAIL_MAX_TICKS = 8;

/** Position of a coverage percentage on the log-tail axis; rises with coverage. */
export function coverageToTail(coverage: number): number {
  return -Math.log10(Math.max(100 - coverage, TAIL_FLOOR));
}

export function tailToCoverage(tail: number): number {
  return 100 - Math.pow(10, -tail);
}

function extent(values: number[]): AxisWindow | null {
  let min = Infinity;
  let max = -Infinity;
  for (const v of values) {
    if (!Number.isFinite(v)) continue;
    if (v < min) min = v;
    if (v > max) max = v;
  }
  return min <= max ? { min, max } : null;
}

function pad(range: AxisWindow, ratio: number, minSpan: number): AxisWindow {
  const span = Math.max(range.max - range.min, minSpan);
  const padding = span * ratio;
  const centre = (range.min + range.max) / 2;
  return { min: Math.min(range.min, centre - span / 2) - padding, max: Math.max(range.max, centre + span / 2) + padding };
}

/** Clamps a padded window into [floor, ceiling], shifting rather than squashing when one edge overflows. */
function clamp(window: AxisWindow, floor: number, ceiling: number): AxisWindow {
  let { min, max } = window;
  if (max > ceiling) {
    min -= max - ceiling;
    max = ceiling;
  }
  if (min < floor) {
    max = Math.min(ceiling, max + (floor - min));
    min = floor;
  }
  return { min, max };
}

/** Linear y window fitted around the plotted coverage values, in percentage points. */
export function coverageWindow(values: number[]): AxisWindow {
  const range = extent(values) ?? { min: 0, max: 100 };
  return clamp(pad(range, LINEAR_PAD, LINEAR_MIN_SPAN), 0, 100);
}

/** Decimal places that keep neighbouring linear ticks distinguishable across the window. */
export function coverageTickDecimals(window: AxisWindow): number {
  const step = (window.max - window.min) / 10;
  return Math.max(0, Math.ceil(-Math.log10(step)));
}

export function formatCoverageTick(coverage: number, decimals = 0): string {
  return `${coverage.toFixed(decimals)}%`;
}

/** Tail-space window fitted around the transformed values; never wider than the data plus padding. */
export function tailWindow(values: number[]): AxisWindow {
  const range = extent(values.map(coverageToTail)) ?? { min: coverageToTail(0), max: coverageToTail(100) };
  return clamp(pad(range, TAIL_PAD, TAIL_MIN_SPAN), coverageToTail(0), coverageToTail(100));
}

/** Smallest 1/2/2.5/5 x 10^k at or above `raw`. */
function niceStep(raw: number): number {
  const magnitude = Math.pow(10, Math.floor(Math.log10(raw)));
  for (const m of [1, 2, 2.5, 5, 10]) if (m * magnitude >= raw) return m * magnitude;
  return 10 * magnitude;
}

/** Remainders (100 - coverage) at 1/2/5 mantissas across every decade the range touches. */
function logRemainders(low: number, high: number): number[] {
  const out: number[] = [];
  for (let e = Math.floor(Math.log10(low)); e <= Math.ceil(Math.log10(high)); e++) {
    for (const m of [1, 2, 5]) {
      const r = m * Math.pow(10, e);
      if (r >= low && r <= high) out.push(r);
    }
  }
  return out;
}

/** Evenly spaced remainders for a range too narrow for whole decades to matter. */
function linearRemainders(low: number, high: number): number[] {
  const step = niceStep((high - low) / TAIL_TARGET_TICKS);
  const out: number[] = [];
  for (let r = Math.ceil(low / step) * step; r <= high + step * 1e-9; r += step) out.push(parseFloat(r.toPrecision(12)));
  return out;
}

/**

 */
export function tailTicks(window: AxisWindow): AxisTick[] {
  const high = Math.min(100, Math.pow(10, -window.min));
  const low = Math.max(TAIL_FLOOR, Math.pow(10, -window.max));
  let remainders = high / low >= 4 ? logRemainders(low, high) : [];
  if (remainders.length < 3) remainders = linearRemainders(low, high);
  const minGap = (window.max - window.min) / TAIL_MAX_TICKS;
  const ticks: AxisTick[] = [];
  let last = -Infinity;
  for (const r of remainders.sort((a, b) => b - a)) {
    const u = coverageToTail(100 - r);
    if (u - last < minGap) continue;
    ticks.push({ value: u, label: formatTailCoverage(100 - r) });
    last = u;
  }
  return ticks;
}

/** Prints a coverage value with only the decimals it needs: 98%, 99.5%, 99.95%. */
export function formatTailCoverage(coverage: number): string {
  return `${parseFloat(coverage.toFixed(2))}%`;
}
