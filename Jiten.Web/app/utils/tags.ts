/** Hand-added tags are a deliberate judgement, not a provider's weighted guess, so they start at full weight. */
export const DEFAULT_TAG_PERCENTAGE = 100;

/** Mirrors NotOriginallyJpTagId in MetadataProviderHelper.Mapping.cs, which seeds it at 100%. */
export const NOT_ORIGINALLY_JP_TAG_ID = 249;

export const NOT_ORIGINALLY_JP_FALLBACK_NAME = 'Not originally Japanese';

export function clampTagPercentage(value: number | null | undefined): number {
  return Math.min(100, Math.max(0, Math.round(value ?? 0)));
}

export const TAG_WEIGHT_LEVELS = [
  { label: 'Minor', value: 33 },
  { label: 'Moderate', value: 66 },
  { label: 'Major', value: 100 },
] as const;

/** Provider-imported tags carry arbitrary percentages, so they display as the nearest level until someone picks one. */
export function nearestTagWeight(percentage: number): number {
  return TAG_WEIGHT_LEVELS.reduce((best, level) => (Math.abs(level.value - percentage) < Math.abs(best - percentage) ? level.value : best), TAG_WEIGHT_LEVELS[0].value);
}

export function tagWeightText(percentage: number): string {
  const level = TAG_WEIGHT_LEVELS.find((l) => l.value === percentage);
  return level ? level.label.toLowerCase() : `${percentage}%`;
}

function tagMatchRank(name: string, query: string): number {
  if (name === query) return 0;
  if (name.startsWith(query)) return 1;
  const index = name.indexOf(query);
  if (index < 0) return -1;
  return /[\s\-/(]/.test(name[index - 1] ?? '') ? 2 : 3;
}

/** Orders exact, then prefix, then word-start, then substring matches, so the likeliest tag sits first for Enter. */
export function searchTags<T extends { name: string }>(tags: readonly T[], query: string): T[] {
  const needle = query.trim().toLowerCase();
  if (!needle) return [...tags].sort((a, b) => a.name.localeCompare(b.name));
  return tags
    .map((tag) => ({ tag, rank: tagMatchRank(tag.name.toLowerCase(), needle) }))
    .filter((entry) => entry.rank >= 0)
    .sort((a, b) => a.rank - b.rank || a.tag.name.localeCompare(b.tag.name))
    .map((entry) => entry.tag);
}

export interface TagNameSegments {
  before: string;
  match: string;
  after: string;
}

export function splitTagMatch(name: string, query: string): TagNameSegments {
  const needle = query.trim().toLowerCase();
  const index = needle ? name.toLowerCase().indexOf(needle) : -1;
  if (index < 0) return { before: name, match: '', after: '' };
  return { before: name.slice(0, index), match: name.slice(index, index + needle.length), after: name.slice(index + needle.length) };
}
