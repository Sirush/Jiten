import type { JourneyGranularity } from '~/types';
import { formatDateLong, formatDateShort, formatDayMonth, formatMonthYear } from '~/utils/dateFormat';

// Bucket dates arrive as bare ISO days; the explicit midnight keeps them from shifting a day west of UTC.
function parseBucket(iso: string): Date {
  return new Date(iso + 'T00:00:00');
}

/** Compact form for chart axis ticks, where the year is implied by the neighbouring labels. */
export function formatBucketAxis(iso: string, granularity: JourneyGranularity): string {
  const date = parseBucket(iso);
  return granularity === 'weekly' ? formatDayMonth(date) : formatMonthYear(date);
}

/** Self-contained form for milestone chips and exported images, which are read without the axis around them. */
export function formatBucketDated(iso: string, granularity: JourneyGranularity): string {
  const date = parseBucket(iso);
  return granularity === 'weekly' ? formatDateShort(date) : formatMonthYear(date);
}

export function formatBucketLong(iso: string, granularity: JourneyGranularity): string {
  const date = parseBucket(iso);
  return granularity === 'weekly' ? `Week of ${formatDateLong(date)}` : formatMonthYear(date, { long: true });
}

export function formatBucketMonthLong(iso: string): string {
  return formatMonthYear(parseBucket(iso), { long: true });
}
