import { describe, expect, it } from 'vitest';
import { jpdbImportLimitReason, type JpdbImportExistingDeck } from '../app/utils/jpdbImportLimits';

const limits = { studyDecks: 3, studyDeckWords: 1000, importWords: 500 };
const list = (name: string, totalWords: number): JpdbImportExistingDeck => ({ name, isWordList: true, totalWords });
const media = (name: string): JpdbImportExistingDeck => ({ name, isWordList: false, totalWords: 9999 });

describe('jpdbImportLimitReason', () => {
  it('passes a selection that fits', () => {
    expect(jpdbImportLimitReason([{ name: 'A', vocabularyCount: 200 }], [list('B', 100)], limits)).toBe('');
  });

  it('names a single deck over the per-deck word limit', () => {
    expect(jpdbImportLimitReason([{ name: 'Core', vocabularyCount: 501 }], [], limits)).toContain('"Core"');
  });

  it('counts several decks over the per-deck word limit', () => {
    const reason = jpdbImportLimitReason(
      [
        { name: 'A', vocabularyCount: 600 },
        { name: 'B', vocabularyCount: 700 },
      ],
      [],
      limits
    );
    expect(reason).toMatch(/^2 selected decks/);
  });

  it('rejects more new lists than free deck slots, counting every deck type', () => {
    const reason = jpdbImportLimitReason(
      [
        { name: 'A', vocabularyCount: 1 },
        { name: 'B', vocabularyCount: 1 },
      ],
      [media('M'), list('L', 0)],
      limits
    );
    expect(reason).toContain('only 1 more fits');
  });

  it('lets replacing decks through when no slots are left', () => {
    const existing = [media('M'), list('L', 10), list('K', 10)];
    expect(jpdbImportLimitReason([{ name: 'L', vocabularyCount: 10 }], existing, limits)).toBe('');
    expect(jpdbImportLimitReason([{ name: 'New', vocabularyCount: 10 }], existing, limits)).toContain('3-deck limit');
  });

  it('treats same-named JPDB decks as one list', () => {
    const selected = [
      { name: 'A', vocabularyCount: 100 },
      { name: ' A ', vocabularyCount: 400 },
    ];
    expect(jpdbImportLimitReason(selected, [media('M'), list('L', 0)], limits)).toBe('');
  });

  it('excludes replaced lists from the word total', () => {
    expect(jpdbImportLimitReason([{ name: 'L', vocabularyCount: 450 }], [list('L', 900)], limits)).toBe('');
    expect(jpdbImportLimitReason([{ name: 'N', vocabularyCount: 450 }], [list('L', 900)], limits)).toMatch(/1.?350 words/);
  });
});
