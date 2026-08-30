export const MEDIA_FILTER_PRESETS_ENDPOINT = 'user/settings/media-filter-presets';
export const MAX_MEDIA_FILTER_PRESETS = 50;
export const MAX_PRESET_NAME_LENGTH = 40;

/** Every URL key the media browser owns. Anything outside this list survives an apply untouched. */
export const PRESET_QUERY_KEYS = [
  'mediaType',
  'title',
  'sortBy',
  'sortOrder',
  'status',
  'charCountMin',
  'charCountMax',
  'difficultyMin',
  'difficultyMax',
  'releaseYearMin',
  'releaseYearMax',
  'uniqueKanjiMin',
  'uniqueKanjiMax',
  'subdeckCountMin',
  'subdeckCountMax',
  'extRatingMin',
  'extRatingMax',
  'speechSpeedMin',
  'speechSpeedMax',
  'speechDurationMin',
  'speechDurationMax',
  'coverageMin',
  'coverageMax',
  'uniqueCoverageMin',
  'uniqueCoverageMax',
  'totalCoverageMin',
  'totalCoverageMax',
  'uTotalCoverageMin',
  'uTotalCoverageMax',
  'genres',
  'excludeGenres',
  'tags',
  'excludeTags',
  'excludeSequels',
] as const;

export type PresetQuery = Record<string, string>;

/** What a router query can hold on the way in and out of an apply. */
export type PresetQueryValue = string | number | null | undefined | (string | null)[];

export interface MediaFilterPreset {
  name: string;
  query: PresetQuery;
  createdAt: number;
}

export interface MediaFilterPresetsState {
  presets: MediaFilterPreset[];
  defaultName: string | null;
}

/** Wire shape of the media-filter-presets settings document. */
export interface MediaFilterPresetsPayload {
  presets: MediaFilterPreset[];
  defaultPreset: string | null;
}

export type SaveStatus = 'saved' | 'replaced' | 'full';
export type RenameStatus = 'renamed' | 'missing' | 'duplicate';

const firstValue = (value: unknown): string | null => {
  const single = Array.isArray(value) ? value[0] : value;
  if (single === null || single === undefined || single === '') return null;
  if (typeof single === 'string') return single;
  if (typeof single === 'number' || typeof single === 'boolean') return String(single);
  return null;
};

export const normalisePresetName = (name: string): string => name.trim().slice(0, MAX_PRESET_NAME_LENGTH);

const sameName = (a: string, b: string) => a.trim().toLowerCase() === b.trim().toLowerCase();

/** Keeps only the keys the browser owns, as strings; `offset` and anything unrecognised is dropped. */
export function capturePresetQuery(source: Record<string, unknown>): PresetQuery {
  const query: PresetQuery = {};
  for (const key of PRESET_QUERY_KEYS) {
    const value = firstValue(source[key]);
    if (value !== null) query[key] = value;
  }
  return query;
}

export function presetQueryEquals(a: PresetQuery, b: PresetQuery, ignoreKeys: readonly string[] = []): boolean {
  for (const key of PRESET_QUERY_KEYS) {
    if (ignoreKeys.includes(key)) continue;
    if ((a[key] ?? '') !== (b[key] ?? '')) return false;
  }
  return true;
}

/**
 * Merges a preset over the live query: every owned key is replaced (or cleared), the rest is kept,
 * and paging restarts. The caller passes the whole result to a single `router.replace`.
 */
export function buildPresetQuery(current: Record<string, PresetQueryValue>, preset: MediaFilterPreset): Record<string, PresetQueryValue> {
  const next: Record<string, PresetQueryValue> = { ...current };
  for (const key of PRESET_QUERY_KEYS) {
    next[key] = preset.query[key] ?? undefined;
  }
  next.offset = 0;
  return next;
}

/** Anything the server (or an older client) stored that is not a usable preset is dropped, never thrown on. */
export function sanitisePresetList(value: unknown): MediaFilterPreset[] {
  if (!Array.isArray(value)) return [];

  const presets: MediaFilterPreset[] = [];
  for (const entry of value) {
    if (presets.length >= MAX_MEDIA_FILTER_PRESETS) break;
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) continue;
    const { name, query, createdAt } = entry as { name?: unknown; query?: unknown; createdAt?: unknown };
    if (typeof name !== 'string' || !name.trim()) continue;
    if (!query || typeof query !== 'object' || Array.isArray(query)) continue;
    if (presets.some((existing) => sameName(existing.name, name))) continue;
    presets.push({
      name: normalisePresetName(name),
      query: capturePresetQuery(query as Record<string, unknown>),
      createdAt: typeof createdAt === 'number' && Number.isFinite(createdAt) ? createdAt : 0,
    });
  }
  return presets;
}

export function savePresetInto(presets: MediaFilterPreset[], name: string, query: PresetQuery): { presets: MediaFilterPreset[]; status: SaveStatus } {
  const cleanName = normalisePresetName(name);
  if (!cleanName) return { presets, status: 'full' };

  const index = presets.findIndex((preset) => sameName(preset.name, cleanName));
  if (index >= 0) {
    const next = [...presets];
    next[index] = { name: cleanName, query: { ...query }, createdAt: presets[index]!.createdAt || Date.now() };
    return { presets: next, status: 'replaced' };
  }
  if (presets.length >= MAX_MEDIA_FILTER_PRESETS) return { presets, status: 'full' };
  return { presets: [...presets, { name: cleanName, query: { ...query }, createdAt: Date.now() }], status: 'saved' };
}

export function renamePresetIn(presets: MediaFilterPreset[], from: string, to: string): { presets: MediaFilterPreset[]; status: RenameStatus } {
  const cleanName = normalisePresetName(to);
  const index = presets.findIndex((preset) => sameName(preset.name, from));
  if (!cleanName || index < 0) return { presets, status: 'missing' };
  if (presets.some((preset, i) => i !== index && sameName(preset.name, cleanName))) return { presets, status: 'duplicate' };

  const next = [...presets];
  next[index] = { ...presets[index]!, name: cleanName };
  return { presets: next, status: 'renamed' };
}

export function deletePresetFrom(presets: MediaFilterPreset[], name: string): MediaFilterPreset[] {
  return presets.filter((preset) => !sameName(preset.name, name));
}

/** A default pointing at a deleted preset resolves to nothing rather than resurrecting it. */
export function resolveDefaultPreset(presets: MediaFilterPreset[], defaultName: string | null): MediaFilterPreset | null {
  if (!defaultName) return null;
  return presets.find((preset) => sameName(preset.name, defaultName)) ?? null;
}

export function parsePresetsResponse(payload: unknown): MediaFilterPresetsState {
  if (!payload || typeof payload !== 'object' || Array.isArray(payload)) return { presets: [], defaultName: null };

  const { presets: rawPresets, defaultPreset } = payload as { presets?: unknown; defaultPreset?: unknown };
  const presets = sanitisePresetList(rawPresets);
  const defaultName = typeof defaultPreset === 'string' && defaultPreset.trim() ? defaultPreset : null;

  return { presets, defaultName: resolveDefaultPreset(presets, defaultName)?.name ?? null };
}

export function toPresetsPayload(state: MediaFilterPresetsState): MediaFilterPresetsPayload {
  return { presets: state.presets.slice(0, MAX_MEDIA_FILTER_PRESETS), defaultPreset: state.defaultName };
}
