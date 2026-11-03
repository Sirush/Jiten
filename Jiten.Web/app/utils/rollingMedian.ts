/** Below this many dots a rolling median mostly retraces the dots themselves. */
export const TREND_MIN_POINTS = 6;

/** Odd window of about an eighth of the series, at least five, so the line smooths single outliers without flattening real shifts. */
export function trendWindow(count: number): number {
  const size = Math.max(5, Math.round(count / 8));
  return size % 2 === 0 ? size + 1 : size;
}

/** Median of each value's window, kept at full size near the ends by sliding it inward rather than shrinking it. */
export function rollingMedian(values: number[], window = trendWindow(values.length)): number[] {
  const size = Math.min(window, values.length);
  const half = Math.floor(size / 2);
  return values.map((_, i) => {
    const start = Math.min(Math.max(0, i - half), values.length - size);
    const sorted = values.slice(start, start + size).sort((a, b) => a - b);
    const mid = Math.floor(size / 2);
    return size % 2 ? sorted[mid]! : (sorted[mid - 1]! + sorted[mid]!) / 2;
  });
}
