import type { Ref } from 'vue';
import { formatRangeSummary, type RangeBounds } from '~/utils/rangeFilters';

export type MediaRangeKey =
  | 'charCount'
  | 'uniqueKanji'
  | 'subdeckCount'
  | 'difficulty'
  | 'coverage'
  | 'totalCoverage'
  | 'uniqueCoverage'
  | 'uTotalCoverage'
  | 'releaseYear'
  | 'extRating'
  | 'speechSpeed'
  | 'speechDuration'
  | 'runtime';

export type MediaRangeSection = 'content' | 'difficulty' | 'media' | 'audio';

export type MediaRangeSpec = {
  key: MediaRangeKey;
  label: string;
  chipLabel: string;
  section: MediaRangeSection;
  floor: number;
  ceil: number;
  step?: number;
  fractionDigits?: number;
  grouping?: boolean;
  suffix?: string;
  requiresAuth?: boolean;
  hint?: string;
};

export const MEDIA_RANGE_SECTIONS: { key: MediaRangeSection; label: string }[] = [
  { key: 'content', label: 'Content' },
  { key: 'difficulty', label: 'Difficulty' },
  { key: 'media', label: 'Media Properties' },
  { key: 'audio', label: 'Audio' },
];

const COVERAGE = { section: 'difficulty', floor: 0, ceil: 100, suffix: '%', grouping: false, requiresAuth: true } as const;

export const MEDIA_RANGE_SPECS: MediaRangeSpec[] = [
  { key: 'charCount', label: 'Character count', chipLabel: 'Characters', section: 'content', floor: 0, ceil: 20_000_000, step: 10_000 },
  { key: 'uniqueKanji', label: 'Unique kanji', chipLabel: 'Unique kanji', section: 'content', floor: 0, ceil: 5000, step: 10 },
  { key: 'subdeckCount', label: 'Subdecks', chipLabel: 'Subdecks', section: 'content', floor: 0, ceil: 2000 },
  { key: 'difficulty', label: 'Difficulty', chipLabel: 'Difficulty', section: 'difficulty', floor: 0, ceil: 5, step: 0.5, fractionDigits: 1, grouping: false },
  { key: 'coverage', label: 'Coverage (Mature)', chipLabel: 'Coverage (Mature)', ...COVERAGE },
  { key: 'totalCoverage', label: 'Coverage (Total)', chipLabel: 'Coverage (Total)', ...COVERAGE },
  { key: 'uniqueCoverage', label: 'Unique Coverage (Mature)', chipLabel: 'Unique coverage (Mature)', ...COVERAGE },
  { key: 'uTotalCoverage', label: 'Unique Coverage (Total)', chipLabel: 'Unique coverage (Total)', ...COVERAGE },
  { key: 'releaseYear', label: 'Release year', chipLabel: 'Year', section: 'media', floor: 1900, ceil: new Date().getFullYear(), grouping: false },
  {
    key: 'extRating',
    label: 'External rating',
    chipLabel: 'Rating',
    section: 'media',
    floor: 0,
    ceil: 100,
    grouping: false,
    hint: '0 means no rating is known',
  },
  { key: 'speechSpeed', label: 'Speech speed', chipLabel: 'Speech speed', section: 'audio', floor: 0, ceil: 800, step: 10 },
  { key: 'speechDuration', label: 'Speech duration', chipLabel: 'Duration', section: 'audio', floor: 0, ceil: 300, suffix: 'h', grouping: false },
];

export const formatRangeValue = (spec: MediaRangeSpec, value: number): string => {
  const digits = spec.fractionDigits ?? 0;
  const text =
    spec.grouping === false
      ? value.toFixed(digits)
      : value.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
  return `${text}${spec.suffix ?? ''}`;
};

export const rangeSummary = (spec: MediaRangeSpec, bounds: RangeBounds): string | null =>
  formatRangeSummary(bounds.min, bounds.max, (value) => formatRangeValue(spec, value));

export type MediaRangeRefs = Record<MediaRangeKey, { min: Ref<number | null>; max: Ref<number | null> }>;

export const readRangeBounds = (refs: MediaRangeRefs): Record<MediaRangeKey, RangeBounds> =>
  Object.fromEntries(MEDIA_RANGE_SPECS.map((spec) => [spec.key, { min: refs[spec.key].min.value, max: refs[spec.key].max.value }])) as Record<
    MediaRangeKey,
    RangeBounds
  >;

export const buildRangeChips = (ranges: Record<MediaRangeKey, RangeBounds>): { key: MediaRangeKey; label: string }[] =>
  MEDIA_RANGE_SPECS.flatMap((spec) => {
    const summary = rangeSummary(spec, ranges[spec.key]);
    return summary === null ? [] : [{ key: spec.key, label: `${spec.chipLabel} ${summary}` }];
  });

export type MediaFilterSnapshot = {
  ranges: Record<MediaRangeKey, RangeBounds>;
  statusFilter: string;
  includeGenres: number[];
  excludeGenres: number[];
  includeTags: number[];
  excludeTags: number[];
  excludeSequels: boolean | null;
  favourite: boolean | null;
};

export const countActiveFilters = (snapshot: MediaFilterSnapshot): number => {
  let count = 0;
  for (const spec of MEDIA_RANGE_SPECS) {
    const bounds = snapshot.ranges[spec.key];
    if (bounds && (bounds.min != null || bounds.max != null)) count++;
  }
  if (snapshot.statusFilter !== 'none') count++;
  if (snapshot.excludeSequels) count++;
  if (snapshot.favourite) count++;
  count += snapshot.includeGenres.length + snapshot.excludeGenres.length;
  count += snapshot.includeTags.length + snapshot.excludeTags.length;
  return count;
};

export const sliderBounds = (spec: MediaRangeSpec, low: number, high: number): RangeBounds => ({
  min: low <= spec.floor ? null : low,
  max: high >= spec.ceil ? null : high,
});
