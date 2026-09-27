import { describe, expect, it } from 'vitest';
import {
  DEFAULT_DISPLAY_VALUES,
  DISPLAY_SECTION_KEYS,
  DISPLAY_VALUE_KEYS,
  displayValuesEqual,
  exportDisplayProfile,
  isFontFamilyName,
  localSettingsDiffer,
  parseDisplayProfileImport,
  pickActiveProfile,
  sanitiseDisplayValues,
  type DisplayProfile,
} from '../app/utils/displayProfile';

const profile = (id: string, values: DisplayProfile['values'] = {}, statColumns: DisplayProfile['statColumns'] = null): DisplayProfile => ({
  id,
  name: id,
  values,
  statColumns,
  updatedAt: 1,
});

describe('sanitiseDisplayValues', () => {
  it('keeps valid values and drops unknown keys and bad values', () => {
    const result = sanitiseDisplayValues({
      titleLanguage: 2,
      headwordFurigana: 'unknown',
      readingSpeed: 50,
      hideTags: 'yes',
      japaneseFont: 'comic-sans',
      evil: '<script>',
      stateColours: { new: 'red' },
    });

    expect(result).toEqual({ titleLanguage: 2, headwordFurigana: 'unknown' });
  });

  it('accepts a complete colour set with nulls and rejects a partial one', () => {
    const colours = { new: '#ff0000', young: null, due: '#00ff00', mature: null, redundant: '#0000ff', ignored: null };

    expect(sanitiseDisplayValues({ stateColours: colours }).stateColours).toEqual(colours);
    expect(sanitiseDisplayValues({ stateColours: { new: '#ff0000' } })).toEqual({});
  });

  it('accepts every default value', () => {
    expect(sanitiseDisplayValues(DEFAULT_DISPLAY_VALUES)).toEqual(DEFAULT_DISPLAY_VALUES);
    expect(Object.keys(DEFAULT_DISPLAY_VALUES).sort()).toEqual([...DISPLAY_VALUE_KEYS].sort());
  });

  it('returns nothing for non-objects', () => {
    expect(sanitiseDisplayValues(null)).toEqual({});
    expect(sanitiseDisplayValues([1, 2])).toEqual({});
    expect(sanitiseDisplayValues('titleLanguage')).toEqual({});
  });
});

describe('displayValuesEqual', () => {
  it('ignores key order, including inside the colour set', () => {
    const reordered = { ...DEFAULT_DISPLAY_VALUES, stateColours: { ...DEFAULT_DISPLAY_VALUES.stateColours } };
    expect(displayValuesEqual(DEFAULT_DISPLAY_VALUES, reordered)).toBe(true);
    expect(displayValuesEqual(DEFAULT_DISPLAY_VALUES, { ...DEFAULT_DISPLAY_VALUES, headwordSize: 'xl' })).toBe(false);
  });
});

describe('pickActiveProfile', () => {
  const profiles = [profile('desk'), profile('phone')];

  it('uses the device profile when it still exists', () => {
    expect(pickActiveProfile(profiles, 'phone')?.id).toBe('phone');
  });

  it('falls back to the first profile when the remembered one is gone or unset', () => {
    expect(pickActiveProfile(profiles, 'deleted')?.id).toBe('desk');
    expect(pickActiveProfile(profiles, null)?.id).toBe('desk');
  });

  it('returns null with no profiles', () => {
    expect(pickActiveProfile([], 'desk')).toBeNull();
  });
});

describe('localSettingsDiffer', () => {
  it('treats keys the profile lacks as their defaults', () => {
    expect(localSettingsDiffer({ values: DEFAULT_DISPLAY_VALUES, statColumns: null }, profile('desk'))).toBe(false);
  });

  it('spots a changed value or changed stat columns', () => {
    const local = { values: { ...DEFAULT_DISPLAY_VALUES, japaneseFont: 'kyokasho' as const }, statColumns: null };
    expect(localSettingsDiffer(local, profile('desk'))).toBe(true);
    expect(localSettingsDiffer({ values: DEFAULT_DISPLAY_VALUES, statColumns: [['wordCount'], [], []] }, profile('desk'))).toBe(true);
  });
});

describe('profile export and import', () => {
  it('round-trips a profile', () => {
    const values = { ...DEFAULT_DISPLAY_VALUES, headwordFurigana: 'hidden' as const };
    const parsed = parseDisplayProfileImport(exportDisplayProfile('Phone', values, [['difficulty'], [], ['wordCount']]));

    expect(parsed).toEqual({ name: 'Phone', values, statColumns: [['difficulty'], [], ['wordCount']] });
  });

  it('drops unknown keys and stats from a hand-edited file', () => {
    const text = JSON.stringify({
      format: 'jiten-display-profile',
      version: 2,
      name: 'Edited',
      values: { titleLanguage: 0, bogus: true, sentenceSize: 'huge' },
      statColumns: [['wordCount', 'nope'], ['wordCount']],
    });

    expect(parseDisplayProfileImport(text)).toEqual({ name: 'Edited', values: { titleLanguage: 0 }, statColumns: [['wordCount'], [], []] });
  });

  it('rejects files that are not display profiles', () => {
    expect(parseDisplayProfileImport('not json')).toBeNull();
    expect(parseDisplayProfileImport(JSON.stringify({ values: {} }))).toBeNull();
  });

  it('names an unnamed import and trims long names', () => {
    expect(parseDisplayProfileImport(JSON.stringify({ format: 'jiten-display-profile' }))?.name).toBe('Imported profile');
    expect(parseDisplayProfileImport(JSON.stringify({ format: 'jiten-display-profile', name: 'x'.repeat(60) }))?.name).toHaveLength(40);
  });
});

describe('DISPLAY_SECTION_KEYS', () => {
  it('puts every display value in exactly one section, so each has a Reset', () => {
    const placed = Object.values(DISPLAY_SECTION_KEYS).flat();
    expect([...placed].sort()).toEqual([...DISPLAY_VALUE_KEYS].sort());
  });
});

describe('isFontFamilyName', () => {
  it('accepts installed font names in any script', () => {
    for (const name of ['Meiryo', 'Hiragino Sans W3', '游ゴシック', 'BIZ UDPGothic', 'Font_Name-2.0', '']) expect(isFontFamilyName(name)).toBe(true);
  });

  it('rejects anything that could break out of a quoted CSS string', () => {
    for (const name of ['Evil"; } body { display: none', "a'b", 'back\\slash', 'semi;colon', 'x'.repeat(81), 42]) expect(isFontFamilyName(name)).toBe(false);
  });
});

describe('readingSpeeds', () => {
  it('needs one in-range speed per difficulty', () => {
    expect(sanitiseDisplayValues({ readingSpeeds: [20000, 17000, 14000, 11000, 9000, 7000] }).readingSpeeds).toHaveLength(6);
    expect(sanitiseDisplayValues({ readingSpeeds: [20000, 17000, 14000] })).toEqual({});
    expect(sanitiseDisplayValues({ readingSpeeds: [20000, 17000, 14000, 11000, 9000, 50] })).toEqual({});
    expect(sanitiseDisplayValues({ readingSpeeds: [20000, 17000, 14000, 11000, 9000, 7000.5] })).toEqual({});
  });
});
