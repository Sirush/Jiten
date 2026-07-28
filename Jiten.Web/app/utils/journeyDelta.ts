import type { JourneyPoint, GrowthPoint } from '~/types';
import { formatBucketMonthLong } from './journeyFormat';

const WINDOWS = [
  { days: 365, since: 'in the past year', over: 'Over the past year' },
  { days: 182, since: 'in the past 6 months', over: 'Over the past 6 months' },
  { days: 91, since: 'in the past 3 months', over: 'Over the past 3 months' },
  { days: 30, since: 'in the past month', over: 'Over the past month' },
];

export interface JourneyWindow {
  fromDate: string;
  fromValue: number;
  currentValue: number;
  delta: number;
  /** Trailing clause: "in the past year", "since April 2025". */
  sinceLabel: string;
  /** Leading clause for a standalone line: "Over the past year", "Since April 2025". */
  overLabel: string;
  wholeHistory: boolean;
}

// UTC, unlike the display formatters: a local-time parse loses an hour across a DST boundary and
// turns a whole number of days into 90.96.
function parseDay(iso: string): number {
  return Date.parse(iso + 'T00:00:00Z') / 86_400_000;
}

export function journeyWindow(points: (JourneyPoint | GrowthPoint)[], value: (point: JourneyPoint | GrowthPoint) => number): JourneyWindow | null {
  if (points.length < 2) return null;

  const first = points[0]!;
  const last = points[points.length - 1]!;
  const span = parseDay(last.date) - parseDay(first.date);
  const window = WINDOWS.find((w) => w.days <= span);

  let anchor = first;
  if (window) {
    const target = parseDay(last.date) - window.days;
    for (const point of points) {
      if (parseDay(point.date) > target) break;
      anchor = point;
    }
  }

  const monthLabel = formatBucketMonthLong(anchor.date);
  return {
    fromDate: anchor.date,
    fromValue: value(anchor),
    currentValue: value(last),
    delta: value(last) - value(anchor),
    sinceLabel: window ? window.since : `since ${monthLabel}`,
    overLabel: window ? window.over : `Since ${monthLabel}`,
    wholeHistory: !window,
  };
}

/** "+23.0 pts in the past year", or a plain statement when the line is flat. */
export function formatJourneyDelta(window: JourneyWindow, unit = 'pts'): string {
  if (Math.abs(window.delta) < 0.05) return `Unchanged ${window.sinceLabel}`;
  const sign = window.delta > 0 ? '+' : '-';
  return `${sign}${Math.abs(window.delta).toFixed(1)} ${unit} ${window.sinceLabel}`;
}
