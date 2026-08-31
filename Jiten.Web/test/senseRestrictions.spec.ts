import { describe, expect, it } from 'vitest';
import type { Definition, Reading } from '../app/types/types';
import { ReadingType } from '../app/types/enums';
import { isSenseRestricted } from '../app/utils/senseRestrictions';

function reading(readingIndex: number, text: string, readingType: ReadingType): Reading {
  return {
    text,
    readingType,
    readingIndex,
    frequencyRank: 0,
    frequencyPercentage: 0,
    usedInMediaAmount: 0,
    usedInMediaAmountByType: {} as Reading['usedInMediaAmountByType'],
  };
}

function sense(restrictedToReadingIndices?: number[]): Definition {
  return { index: 0, meanings: ['x'], partsOfSpeech: [], restrictedToReadingIndices };
}

// 施行 (word 1579510): one kanji form, three readings.
const shikou: Reading[] = [
  reading(0, '施行', ReadingType.Reading),
  reading(1, 'しこう', ReadingType.KanaReading),
  reading(2, 'せこう', ReadingType.KanaReading),
  reading(3, 'しぎょう', ReadingType.KanaReading),
];

// Two kanji spellings, one reading.
const twoSpellings: Reading[] = [
  reading(0, '御田', ReadingType.Reading),
  reading(1, 'おでん', ReadingType.Reading),
  reading(2, 'おでん', ReadingType.KanaReading),
];

describe('isSenseRestricted', () => {
  it('leaves a reading-restricted sense legible on the kanji form', () => {
    expect(isSenseRestricted(sense([1, 2]), 0, shikou)).toBe(false);
  });

  it('still dims on the readings the sense excludes', () => {
    expect(isSenseRestricted(sense([1, 2]), 1, shikou)).toBe(false);
    expect(isSenseRestricted(sense([1, 2]), 3, shikou)).toBe(true);
    expect(isSenseRestricted(sense([1, 3]), 2, shikou)).toBe(true);
  });

  it('dims a kanji-restricted sense on the spelling it excludes, not on the readings', () => {
    expect(isSenseRestricted(sense([0]), 1, twoSpellings)).toBe(true);
    expect(isSenseRestricted(sense([0]), 0, twoSpellings)).toBe(false);
    expect(isSenseRestricted(sense([0]), 2, twoSpellings)).toBe(false);
  });

  it('dims when either axis excludes the current form', () => {
    const bothAxes = sense([0, 2]);
    expect(isSenseRestricted(bothAxes, 1, twoSpellings)).toBe(true);
    expect(isSenseRestricted(bothAxes, 0, twoSpellings)).toBe(false);
    expect(isSenseRestricted(bothAxes, 2, twoSpellings)).toBe(false);
  });

  it('never restricts an unrestricted sense', () => {
    expect(isSenseRestricted(sense(), 0, shikou)).toBe(false);
    expect(isSenseRestricted(sense([]), 3, shikou)).toBe(false);
  });

  it('falls back to the flat check when the current form is not in the readings', () => {
    expect(isSenseRestricted(sense([1, 2]), 0, [])).toBe(true);
    expect(isSenseRestricted(sense([1, 2]), 0, undefined)).toBe(true);
    expect(isSenseRestricted(sense([1, 2]), 2, [])).toBe(false);
  });

  it('does nothing without a current reading index', () => {
    expect(isSenseRestricted(sense([1, 2]), undefined, shikou)).toBe(false);
  });
});
