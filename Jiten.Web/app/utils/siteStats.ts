import { MediaType } from '~/types/enums';
import type { SiteStatsFrequencyBand, SiteStatsReading } from '~/types/siteStats';
import { formatDateLong, formatMonthShort, formatMonthYear } from '~/utils/dateFormat';

const compactFormatter = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 });

export function formatCompact(value: number): string {
  return value < 10_000 ? value.toLocaleString('en-US') : compactFormatter.format(value);
}

const approxFormatter = new Intl.NumberFormat('en-US', { notation: 'compact', maximumSignificantDigits: 2 });

/** For figures labelled as approximate, so precision beyond two digits would contradict the label. */
export function formatApprox(value: number): string {
  return approxFormatter.format(value);
}

export function roundToHundred(value: number): number {
  return Math.round(value / 100) * 100;
}

export function floorToHundred(value: number): number {
  return Math.floor(value / 100) * 100;
}

export function rankLabel(rank: number): string {
  return rank >= 1000 ? `${rank / 1000}k` : String(rank);
}

/** Bands are plotted on a log axis at their geometric middle, so 1-100 and 15k-20k get comparable widths. */
export function bandPosition(band: Pick<SiteStatsFrequencyBand, 'from' | 'to'>): number {
  return Math.sqrt(Math.max(band.from, 1) * band.to);
}

/** Size-weighted average of the band medians up to `upTo`; null when no band ends inside the range. */
export function medianShareOfTopWords(bands: SiteStatsFrequencyBand[], upTo: number): number | null {
  const inRange = bands.filter((b) => b.to <= upTo);
  const size = inRange.reduce((sum, b) => sum + (b.to - b.from + 1), 0);
  if (size === 0) return null;
  return inRange.reduce((sum, b) => sum + b.median * (b.to - b.from + 1), 0) / size;
}

const FRACTIONS: [number, string][] = [
  [25, 'a quarter'],
  [100 / 3, 'a third'],
  [50, 'half'],
  [200 / 3, 'two thirds'],
  [75, 'three quarters'],
];

export function describeShare(percent: number): string {
  const match = FRACTIONS.find(([value]) => Math.abs(percent - value) <= 2);
  return match ? match[1] : `${Math.round(percent)}%`;
}

const WATCHED_TYPES = new Set<MediaType>([MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.YouTube]);

export function isWatchedType(mediaType: MediaType | null): boolean {
  return mediaType !== null && WATCHED_TYPES.has(mediaType);
}

export interface WatchedFigure {
  value: number;
  unit: string;
}

export function watchedFigure(row: SiteStatsReading): WatchedFigure {
  switch (row.mediaType) {
    case MediaType.Movie:
      return { value: row.titlesFinished, unit: row.titlesFinished === 1 ? 'film' : 'films' };
    case MediaType.YouTube:
      return { value: row.unitsFinished, unit: row.unitsFinished === 1 ? 'video' : 'videos' };
    default:
      return { value: row.unitsFinished, unit: row.unitsFinished === 1 ? 'episode' : 'episodes' };
  }
}

/** Calendar month of a "YYYY-MM-DD" date, read in UTC so the label doesn't shift with the viewer's zone. */
export function formatMonth(isoDate: string, withYear: boolean): string {
  const date = new Date(`${isoDate.slice(0, 10)}T00:00:00Z`);
  return withYear ? formatMonthYear(date, { utc: true }) : formatMonthShort(date, { utc: true });
}

export function formatSnapshotDate(isoDate: string): string {
  const date = new Date(`${isoDate.slice(0, 10)}T00:00:00Z`);
  return formatDateLong(date, { utc: true });
}

/** Five shade steps relative to the largest cell; 0 stays empty. */
export function heatStep(value: number, max: number): number {
  if (value <= 0 || max <= 0) return 0;
  return Math.min(5, Math.ceil((value / max) * 5));
}
