import { describe, expect, it } from 'vitest';
import { buildSubdeckTitles, cleanFileName, detectNumbering, parseKanjiNumeral, stripFileExtension } from '../app/utils/subdeckNumbering';

const values = (names: string[]) => detectNumbering(names).map((d) => d?.value ?? null);
const displays = (names: string[]) => detectNumbering(names).map((d) => d?.display ?? null);

describe('stripFileExtension', () => {
  it('strips normal extensions', () => {
    expect(stripFileExtension('Vol 7.epub')).toBe('Vol 7');
    expect(stripFileExtension('episode.srt')).toBe('episode');
  });

  it('does not eat decimal volume numbers', () => {
    expect(stripFileExtension('Vol 7.5.epub')).toBe('Vol 7.5');
    expect(stripFileExtension('Vol 7.5')).toBe('Vol 7.5');
  });

  it('strips subtitle language double-extensions', () => {
    expect(stripFileExtension('Show - 07.ja.srt')).toBe('Show - 07');
  });
});

describe('parseKanjiNumeral', () => {
  it('parses simple and compositional numerals', () => {
    expect(parseKanjiNumeral('七')).toBe(7);
    expect(parseKanjiNumeral('十三')).toBe(13);
    expect(parseKanjiNumeral('二十三')).toBe(23);
    expect(parseKanjiNumeral('百二十')).toBe(120);
  });

  it('parses positional digit strings', () => {
    expect(parseKanjiNumeral('一九八四')).toBe(1984);
  });

  it('parses daiji (banknote numerals)', () => {
    expect(parseKanjiNumeral('壱')).toBe(1);
    expect(parseKanjiNumeral('弐')).toBe(2);
    expect(parseKanjiNumeral('参')).toBe(3);
    expect(parseKanjiNumeral('拾')).toBe(10);
    expect(parseKanjiNumeral('廿')).toBe(20);
  });

  it('rejects garbage', () => {
    expect(parseKanjiNumeral('猫')).toBe(null);
  });
});

describe('detectNumbering', () => {
  it('handles the motivating case: 7 / 7.5 / 8', () => {
    expect(values(['Vol 7.epub', 'Vol 7.5.epub', 'Vol 8.epub'])).toEqual([7, 7.5, 8]);
  });

  it('handles bare numbers with noisy fansub names', () => {
    const names = [
      '[SubsGroup] Some Show (2020) - 01 [1080p][A1B2C3D4].srt',
      '[SubsGroup] Some Show (2020) - 02 [1080p][99AF31BB].srt',
      '[SubsGroup] Some Show (2020) - 07.5 [1080p][CC01D2E3].srt',
    ];
    expect(values(names)).toEqual([1, 2, 7.5]);
  });

  it('prefers the episode slot in SxxEyy names', () => {
    const names = ['Show S01E01.srt', 'Show S01E02.srt', 'Show S01E02.5.srt', 'Show S01E03.srt'];
    expect(values(names)).toEqual([1, 2, 2.5, 3]);
  });

  it('parses 第N巻 with kanji numerals', () => {
    expect(values(['第一巻.txt', '第二巻.txt', '第十三巻.txt'])).toEqual([1, 2, 13]);
  });

  it('parses daiji volumes: 壱ノ巻 / 弐ノ巻', () => {
    expect(values(['壱ノ巻.txt', '弐ノ巻.txt'])).toEqual([1, 2]);
  });

  it('ignores a constant roman numeral in the title but detects a roman sequence', () => {
    expect(values(['Title VII - 01.txt', 'Title VII - 02.txt'])).toEqual([1, 2]);
    expect(values(['Game I.txt', 'Game II.txt', 'Game III.txt'])).toEqual([1, 2, 3]);
  });

  it('resolves ordinal sets', () => {
    expect(values(['上巻.txt', '下巻.txt'])).toEqual([1, 2]);
    expect(values(['タイトル 上巻.txt', 'タイトル 中巻.txt', 'タイトル 下巻.txt'])).toEqual([1, 2, 3]);
    expect(values(['前編.srt', '後編.srt'])).toEqual([1, 2]);
  });

  it('does not treat a version marker as the number', () => {
    expect(values(['Show EP07v2.srt', 'Show EP08.srt'])).toEqual([7, 8]);
  });

  it('normalizes circled and full-width numbers', () => {
    expect(values(['①.txt', '②.txt', '③.txt'])).toEqual([1, 2, 3]);
    expect(values(['７．５巻.txt', '８巻.txt'])).toEqual([7.5, 8]);
  });

  it('detects ranges', () => {
    expect(displays(['MyBook vol 1-3.epub', 'MyBook vol 4-6.epub'])).toEqual(['1-3', '4-6']);
  });

  it('leaves specials undetected in an otherwise numbered batch', () => {
    const names = ['Show - 01.srt', 'Show - 02.srt', 'Show - 03.srt', 'Show - SP.srt'];
    expect(values(names)).toEqual([1, 2, 3, null]);
  });

  it('returns all null when there is nothing to detect', () => {
    expect(values(['A.txt', 'B.txt'])).toEqual([null, null]);
  });

  it('mixes saved titles and new file names through the same anchor', () => {
    const names = ['Volume 1', 'Volume 2', 'Volume 3', 'MyBook v04.epub', 'MyBook v05.epub', 'MyBook v06.epub'];
    expect(values(names)).toEqual([1, 2, 3, 4, 5, 6]);
  });

  it('single file: only trusts anchored matches', () => {
    expect(values(['第7巻.epub'])).toEqual([7]);
    expect(values(['MyBook vol 12.epub'])).toEqual([12]);
    expect(values(['MyBook 12.epub'])).toEqual([null]);
  });

  it('rejects resolution, year, crc and codec numbers', () => {
    const names = ['[G] Show - 01 (x264 1080p) [AABBCC11].mkv.srt', '[G] Show - 02 (x264 1080p) [DDEE2233].mkv.srt'];
    expect(values(names)).toEqual([1, 2]);
  });

  it('strips leading zeros in displays', () => {
    expect(displays(['Show - 07.srt', 'Show - 08.srt'])).toEqual(['7', '8']);
  });
});

describe('buildSubdeckTitles', () => {
  it('builds titles from detections with sequential fallback', () => {
    const titles = buildSubdeckTitles(['Vol 7.epub', 'Vol 7.5.epub', 'SP.epub'], 'Volume');
    expect(titles).toEqual([
      { title: 'Volume 7', detected: true },
      { title: 'Volume 7.5', detected: true },
      { title: 'Volume 3', detected: false },
    ]);
  });

  it('falls back to filenames when requested', () => {
    const titles = buildSubdeckTitles(['A.txt', 'B.txt'], 'Volume', { fallback: 'filename' });
    expect(titles).toEqual([
      { title: 'A', detected: false },
      { title: 'B', detected: false },
    ]);
  });
});

describe('cleanFileName', () => {
  it('strips extension and collapses separators', () => {
    expect(cleanFileName('My_Book_Vol_7.epub')).toBe('My Book Vol 7');
  });
});
