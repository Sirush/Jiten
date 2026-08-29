import { describe, expect, it } from 'vitest';
import { miscCodes, miscLabel } from '../app/utils/posAbbreviations';

// Mirrors the misc block of _posDictionary in Jiten.Core/Data/JMDict/JMDictHelper.cs. The API ships
// misc as raw entity codes, so a code missing from the frontend table renders its tooltip as the code.
const SERVER_MISC_CODES = [
  'abbr',
  'arch',
  'char',
  'chn',
  'col',
  'company',
  'creat',
  'dated',
  'dei',
  'derog',
  'doc',
  'euph',
  'ev',
  'fam',
  'fem',
  'fict',
  'form',
  'given',
  'group',
  'hist',
  'hon',
  'hum',
  'id',
  'joc',
  'leg',
  'm-sl',
  'male',
  'myth',
  'net-sl',
  'obj',
  'obs',
  'on-mim',
  'organization',
  'oth',
  'person',
  'place',
  'poet',
  'pol',
  'product',
  'proverb',
  'quote',
  'rare',
  'relig',
  'sens',
  'serv',
  'ship',
  'sl',
  'station',
  'surname',
  'uk',
  'unclass',
  'vulg',
  'work',
  'X',
  'yoji',
];

// A handful of JMdict codes are already the word itself; their tooltip legitimately repeats the badge.
const SELF_LABELLING = new Set(['dated', 'group', 'proverb', 'rare']);

describe('miscLabel', () => {
  it('expands the reported code instead of echoing it', () => {
    expect(miscLabel('form')).toBe('formal or literary term');
  });

  it('keeps the shortened house wording', () => {
    expect(miscLabel('uk')).toBe('usu. kana');
    expect(miscLabel('on-mim')).toBe('onomatopoeia');
    expect(miscLabel('X')).toBe('X-rated');
  });

  it('covers every misc code the API can emit', () => {
    const missing = SERVER_MISC_CODES.filter((code) => !miscCodes.includes(code));
    expect(missing).toEqual([]);
  });

  it('never returns the bare code except for the self-labelling ones', () => {
    const echoed = miscCodes.filter((code) => miscLabel(code) === code);
    expect(echoed.sort()).toEqual([...SELF_LABELLING].sort());
  });

  it('falls back to the code for anything unrecognised', () => {
    expect(miscLabel('not-a-real-entity')).toBe('not-a-real-entity');
    expect(miscLabel('')).toBe('');
  });
});
