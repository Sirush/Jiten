import { describe, expect, it } from 'vitest';
import {
  MAX_MEDIA_FILTER_PRESETS,
  type MediaFilterPreset,
  buildPresetQuery,
  capturePresetQuery,
  deletePresetFrom,
  parsePresetsResponse,
  presetQueryEquals,
  renamePresetIn,
  resolveDefaultPreset,
  sanitisePresetList,
  savePresetInto,
  toPresetsPayload,
} from '../app/utils/mediaFilterPresets';

const preset = (name: string, query: Record<string, string> = {}): MediaFilterPreset => ({ name, query, createdAt: 1 });

const fill = (count: number) =>
  Array.from({ length: count }, (_, i) => preset(`Preset ${i}`, { sortBy: 'title' })).reduce(
    (list, entry) => savePresetInto(list, entry.name, entry.query).presets,
    [] as MediaFilterPreset[]
  );

describe('reading the stored settings document', () => {
  it('degrades to no presets for a payload that is not a settings object', () => {
    for (const payload of [null, undefined, '', 'a string', 42, [], [{ name: 'Reading', query: {} }]]) {
      expect(parsePresetsResponse(payload)).toEqual({ presets: [], defaultName: null });
    }
    expect(parsePresetsResponse({})).toEqual({ presets: [], defaultName: null });
    expect(parsePresetsResponse({ presets: 'nope', defaultPreset: 7 })).toEqual({ presets: [], defaultName: null });
  });

  it('reads presets and the default pointer', () => {
    const state = parsePresetsResponse({ presets: [{ name: 'Reading', query: { sortBy: 'title' }, createdAt: 3 }], defaultPreset: 'Reading' });
    expect(state.presets.map((p) => p.name)).toEqual(['Reading']);
    expect(state.defaultName).toBe('Reading');
  });

  it('drops a default pointing at a preset the payload does not carry', () => {
    expect(parsePresetsResponse({ presets: [{ name: 'Reading', query: {} }], defaultPreset: 'Gone' }).defaultName).toBeNull();
  });

  it('drops entries without a usable name or query object', () => {
    const stored = [
      { name: 'Good', query: { sortBy: 'difficulty' }, createdAt: 5 },
      { name: '   ', query: {} },
      { query: { sortBy: 'title' } },
      { name: 'No query' },
      { name: 'Array query', query: ['sortBy'] },
      null,
      'Reading',
    ];
    expect(sanitisePresetList(stored).map((p) => p.name)).toEqual(['Good']);
  });

  it('keeps only keys the browser owns', () => {
    const stored = [{ name: 'Mixed', query: { sortBy: 'title', offset: '40', wordId: '123', evil: { a: 1 } }, createdAt: 2 }];
    expect(sanitisePresetList(stored)[0]!.query).toEqual({ sortBy: 'title' });
  });

  it('keeps the first of two presets sharing a name, and caps the list', () => {
    const stored = [
      { name: 'Reading', query: { sortBy: 'title' } },
      { name: 'reading', query: { sortBy: 'difficulty' } },
      ...Array.from({ length: 60 }, (_, i) => ({ name: `Extra ${i}`, query: {} })),
    ];
    const parsed = sanitisePresetList(stored);
    expect(parsed).toHaveLength(MAX_MEDIA_FILTER_PRESETS);
    expect(parsed[0]).toMatchObject({ name: 'Reading', query: { sortBy: 'title' } });
  });

  it('round-trips a captured query through the wire shape unchanged', () => {
    const query = capturePresetQuery({
      mediaType: 3,
      title: 'ゆゆ式',
      sortBy: 'difficulty',
      sortOrder: 0,
      status: 'planning',
      difficultyMin: 2,
      difficultyMax: 3.5,
      totalCoverageMin: 80,
      uTotalCoverageMax: 95,
      genres: '4,9',
      excludeTags: '249',
      excludeSequels: 'true',
      offset: 40,
    });
    const payload = toPresetsPayload({ presets: [{ name: 'Comfy', query, createdAt: 7 }], defaultName: 'Comfy' });
    const state = parsePresetsResponse(JSON.parse(JSON.stringify(payload)));

    expect(state.presets[0]!.query).toEqual(query);
    expect(state.defaultName).toBe('Comfy');
    expect(query.offset).toBeUndefined();
    expect(query).toMatchObject({ mediaType: '3', sortOrder: '0', difficultyMax: '3.5' });
  });

  it('caps the list it sends back to the server', () => {
    const payload = toPresetsPayload({ presets: fill(MAX_MEDIA_FILTER_PRESETS), defaultName: null });
    expect(payload.presets).toHaveLength(MAX_MEDIA_FILTER_PRESETS);
    expect(payload.defaultPreset).toBeNull();
  });
});

describe('capturing the live query', () => {
  it('drops empty values and takes the first of a repeated param', () => {
    expect(capturePresetQuery({ title: '', sortBy: ['difficulty', 'title'], status: null, genres: undefined })).toEqual({ sortBy: 'difficulty' });
  });

  it('compares only the keys the browser owns', () => {
    expect(presetQueryEquals({ sortBy: 'title' }, { sortBy: 'title' })).toBe(true);
    expect(presetQueryEquals({ sortBy: 'title' }, { sortBy: 'title', status: 'fav' })).toBe(false);
    expect(presetQueryEquals({ sortBy: 'title', title: '' }, { sortBy: 'title' })).toBe(true);
  });

  it('skips ignored keys so an embed match can disregard the media tab', () => {
    expect(presetQueryEquals({ sortBy: 'title', mediaType: '1' }, { sortBy: 'title', mediaType: '3' }, ['mediaType'])).toBe(true);
    expect(presetQueryEquals({ sortBy: 'title', mediaType: '1' }, { sortBy: 'difficulty', mediaType: '3' }, ['mediaType'])).toBe(false);
  });
});

describe('saving', () => {
  it('appends up to the cap and then refuses new names', () => {
    const full = fill(MAX_MEDIA_FILTER_PRESETS);
    expect(full).toHaveLength(MAX_MEDIA_FILTER_PRESETS);

    const result = savePresetInto(full, 'One too many', { sortBy: 'title' });
    expect(result.status).toBe('full');
    expect(result.presets).toHaveLength(MAX_MEDIA_FILTER_PRESETS);
  });

  it('replaces a same-name preset even at the cap', () => {
    const full = fill(MAX_MEDIA_FILTER_PRESETS);
    const result = savePresetInto(full, 'preset 0', { sortBy: 'difficulty' });
    expect(result.status).toBe('replaced');
    expect(result.presets).toHaveLength(MAX_MEDIA_FILTER_PRESETS);
    expect(result.presets[0]).toMatchObject({ name: 'preset 0', query: { sortBy: 'difficulty' } });
  });

  it('trims the name and refuses a blank one', () => {
    expect(savePresetInto([], '  Reading  ', {}).presets[0]!.name).toBe('Reading');
    expect(savePresetInto([], '   ', {}).presets).toEqual([]);
  });
});

describe('renaming and deleting', () => {
  const list = [preset('Reading'), preset('Listening')];

  it('renames in place', () => {
    const result = renamePresetIn(list, 'Reading', 'Novels');
    expect(result.status).toBe('renamed');
    expect(result.presets.map((p) => p.name)).toEqual(['Novels', 'Listening']);
  });

  it('refuses a name another preset already holds', () => {
    expect(renamePresetIn(list, 'Reading', 'listening').status).toBe('duplicate');
  });

  it('reports a rename of something that is gone', () => {
    expect(renamePresetIn(list, 'Gone', 'Novels').status).toBe('missing');
  });

  it('deletes by name regardless of case', () => {
    expect(deletePresetFrom(list, 'reading').map((p) => p.name)).toEqual(['Listening']);
  });
});

describe('the default preset', () => {
  const list = [preset('Reading'), preset('Listening')];

  it('resolves the pointed-at preset', () => {
    expect(resolveDefaultPreset(list, 'Reading')?.name).toBe('Reading');
  });

  it('resolves to nothing when the preset was deleted or never set', () => {
    expect(resolveDefaultPreset(deletePresetFrom(list, 'Reading'), 'Reading')).toBeNull();
    expect(resolveDefaultPreset(list, null)).toBeNull();
  });
});

describe('building the applied query', () => {
  const saved = preset('Hard novels', { mediaType: '7', sortBy: 'difficulty', sortOrder: '1', difficultyMin: '4' });

  it("carries the preset's own sort order rather than the sort's default", () => {
    const applied = buildPresetQuery({ sortBy: 'title', sortOrder: '0' }, saved);
    expect(applied.sortBy).toBe('difficulty');
    expect(applied.sortOrder).toBe('1');
  });

  it('clears the filters the preset does not carry and restarts paging', () => {
    const applied = buildPresetQuery({ sortBy: 'title', status: 'fav', genres: '4', offset: '80', charCountMin: '1000' }, saved);
    expect(applied.status).toBeUndefined();
    expect(applied.genres).toBeUndefined();
    expect(applied.charCountMin).toBeUndefined();
    expect(applied.offset).toBe(0);
  });

  it('leaves keys the browser does not own alone', () => {
    expect(buildPresetQuery({ wordId: '1234' }, saved).wordId).toBe('1234');
  });

  it('reapplies to itself unchanged', () => {
    const applied = buildPresetQuery({}, saved);
    expect(capturePresetQuery(applied)).toEqual(saved.query);
  });
});
