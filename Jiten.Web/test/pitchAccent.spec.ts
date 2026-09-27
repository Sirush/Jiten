import { describe, expect, it } from 'vitest';
import { moraCount, pitchCategory, pitchColourClasses, readingKana, wordPitchCategory } from '../app/utils/pitchAccent';

describe('readingKana', () => {
  it('keeps the readings of ruby markup and the okurigana around them', () => {
    expect(readingKana('食[た]べる')).toBe('たべる');
    expect(readingKana('お茶[ちゃ]')).toBe('おちゃ');
    expect(readingKana('一[いっ]ヶ月[かげつ]')).toBe('いっかげつ');
  });

  it('passes plain kana through', () => {
    expect(readingKana('りんご')).toBe('りんご');
    expect(readingKana('コーヒー')).toBe('コーヒー');
  });
});

describe('moraCount', () => {
  it('folds small kana into the mora before them but counts っ and ー', () => {
    expect(moraCount('きょう')).toBe(2);
    expect(moraCount('がっこう')).toBe(4);
    expect(moraCount('コーヒー')).toBe(4);
    expect(moraCount('ティッシュ')).toBe(3);
  });
});

describe('pitchCategory', () => {
  it('names the four patterns', () => {
    expect(pitchCategory(0, 3)).toBe('heiban');
    expect(pitchCategory(1, 3)).toBe('atamadaka');
    expect(pitchCategory(2, 3)).toBe('nakadaka');
    expect(pitchCategory(3, 3)).toBe('odaka');
  });

  it('rejects an accent past the end of the word', () => {
    expect(pitchCategory(4, 3)).toBeNull();
    expect(pitchCategory(-1, 3)).toBeNull();
  });
});

describe('wordPitchCategory', () => {
  it('uses the reading, not the kanji, to count morae', () => {
    expect(wordPitchCategory('食[た]べる', [2])).toBe('nakadaka');
    expect(wordPitchCategory('箸[はし]', [1])).toBe('atamadaka');
    expect(wordPitchCategory('橋[はし]', [2])).toBe('odaka');
  });

  it('refuses to pick a colour when the accents disagree', () => {
    expect(wordPitchCategory('今日[きょう]', [1, 0])).toBeNull();
    expect(wordPitchCategory('今日[きょう]', [0, 0])).toBe('heiban');
  });

  it('is null without accents', () => {
    expect(wordPitchCategory('猫[ねこ]', [])).toBeNull();
    expect(wordPitchCategory('猫[ねこ]', null)).toBeNull();
  });
});

describe('pitchColourClasses', () => {
  it('colours only the reading of a kanji word, and the whole of a kana word', () => {
    expect(pitchColourClasses('林檎[りんご]', [0])).toBe('pitch-coloured pitch-heiban');
    expect(pitchColourClasses('りんご', [0])).toBe('pitch-coloured pitch-kana pitch-heiban');
    expect(pitchColourClasses('りんご', [])).toBe('');
  });
});
