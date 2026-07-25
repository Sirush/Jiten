import type { JourneyGranularity } from '~/types';

// Bucket dates arrive as bare ISO days; the explicit midnight keeps them from shifting a day west of UTC.
function parseBucket(iso: string): Date {
  return new Date(iso + 'T00:00:00');
}

const axisWeekly = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' });
const datedWeekly = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
const monthAndYear = new Intl.DateTimeFormat(undefined, { month: 'short', year: 'numeric' });
const longDay = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'long', year: 'numeric' });
const longMonth = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' });

/** Compact form for chart axis ticks, where the year is implied by the neighbouring labels. */
export function formatBucketAxis(iso: string, granularity: JourneyGranularity): string {
  return (granularity === 'weekly' ? axisWeekly : monthAndYear).format(parseBucket(iso));
}

/** Self-contained form for milestone chips and exported images, which are read without the axis around them. */
export function formatBucketDated(iso: string, granularity: JourneyGranularity): string {
  return (granularity === 'weekly' ? datedWeekly : monthAndYear).format(parseBucket(iso));
}

export function formatBucketLong(iso: string, granularity: JourneyGranularity): string {
  const date = parseBucket(iso);
  return granularity === 'weekly' ? `Week of ${longDay.format(date)}` : longMonth.format(date);
}

export function formatBucketMonthLong(iso: string): string {
  return longMonth.format(parseBucket(iso));
}
