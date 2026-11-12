import { describe, expect, it } from 'vitest';
import { singleEncodedKanjiPath } from '../server/utils/kanjiPath';

describe('singleEncodedKanjiPath', () => {
  it('unwraps a double-encoded kanji', () => {
    expect(singleEncodedKanjiPath('/kanji/%25E7%25BF%258C')).toBe('/kanji/%E7%BF%8C');
  });

  it('uppercases the hex digits and drops a trailing slash', () => {
    expect(singleEncodedKanjiPath('/kanji/%25e7%25bf%258c/')).toBe('/kanji/%E7%BF%8C');
  });

  it('unwraps a kanji outside the basic plane', () => {
    expect(singleEncodedKanjiPath('/kanji/%25F0%25A0%25AE%259F')).toBe('/kanji/%F0%A0%AE%9F');
  });

  it('leaves correctly encoded and raw paths alone', () => {
    expect(singleEncodedKanjiPath('/kanji/%E7%BF%8C')).toBeNull();
    expect(singleEncodedKanjiPath('/kanji/翌')).toBeNull();
    expect(singleEncodedKanjiPath('/kanji-grid')).toBeNull();
  });

  it('rejects sequences that are not one valid character', () => {
    expect(singleEncodedKanjiPath('/kanji/%25E7%25BF')).toBeNull();
    expect(singleEncodedKanjiPath('/kanji/%25E7%25BF%258C%25E7%25BF%258C')).toBeNull();
    expect(singleEncodedKanjiPath('/kanji/%252F')).toBeNull();
  });
});
