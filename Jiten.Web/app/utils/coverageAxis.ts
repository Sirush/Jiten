export interface AxisWindow {
  min: number;
  max: number;
}

export interface AxisTick {
  value: number;
  label: string;
}

export const TAIL_FLOOR = 0.01;
export const TAIL_ANCHORS = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 95, 98, 99, 99.5, 99.9, 99.95, 99.99];

const LINEAR_PAD = 0.1;
const LINEAR_MIN_SPAN = 1;
const TAIL_PAD = 0.1;
const TAIL_MIN_SPAN = 0.2;

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

/** Tail-space window around the transformed values, widened until at least two ladder anchors fall inside. */
export function tailWindow(values: number[]): AxisWindow {
  const range = extent(values.map(coverageToTail)) ?? { min: coverageToTail(0), max: coverageToTail(100) };
  const window = clamp(pad(range, TAIL_PAD, TAIL_MIN_SPAN), coverageToTail(0), coverageToTail(100));
  const anchors = TAIL_ANCHORS.map(coverageToTail);
  let inside = anchors.filter((a) => a >= window.min && a <= window.max).length;
  let below = anchors.filter((a) => a < window.min).length - 1;
  let above = anchors.findIndex((a) => a > window.max);
  while (inside < 2) {
    if (below >= 0) {
      window.min = anchors[below]!;
      below--;
      inside++;
    } else if (above >= 0 && above < anchors.length) {
      window.max = anchors[above]!;
      above++;
      inside++;
    } else {
      break;
    }
  }
  return window;
}

const TAIL_MAX_TICKS = 12;

/**
 * Ladder anchors inside a tail window, positioned in tail space and labelled as coverage percentages.
 * Thinned from the top down, so a series that starts near 0% sheds the bunched low anchors, never the fine ones.
 */
export function tailTicks(window: AxisWindow): AxisTick[] {
  const minGap = (window.max - window.min) / TAIL_MAX_TICKS;
  const ticks: AxisTick[] = [];
  let last = Infinity;
  for (let i = TAIL_ANCHORS.length - 1; i >= 0; i--) {
    const c = TAIL_ANCHORS[i]!;
    const u = coverageToTail(c);
    if (u < window.min - 1e-9 || u > window.max + 1e-9) continue;
    if (last - u < minGap) continue;
    ticks.push({ value: u, label: formatTailCoverage(c) });
    last = u;
  }
  return ticks.reverse();
}

/** Prints a coverage value with only the decimals it needs: 98%, 99.5%, 99.95%. */
export function formatTailCoverage(coverage: number): string {
  return `${parseFloat(coverage.toFixed(2))}%`;
}
