import { describe, expect, it } from 'vitest';
import { sameTitles, titlesDraft, titlesFromDraft } from '~/utils/groupTitles';
import { franchiseDisplayName } from '~/utils/franchiseLayout';
import type { FranchiseNode } from '~/types';

const english = (t: { originalTitle: string; romajiTitle?: string | null; englishTitle?: string | null }) => t.englishTitle || t.romajiTitle || t.originalTitle;

describe('group title drafts', () => {
  it('trims and sends blank optional titles as null', () => {
    expect(titlesFromDraft({ originalTitle: ' 推しの子 ', romajiTitle: '  ', englishTitle: ' Oshi no Ko ' })).toEqual({
      originalTitle: '推しの子',
      romajiTitle: null,
      englishTitle: 'Oshi no Ko',
    });
  });

  it('starts from stored titles with nulls as empty fields', () => {
    expect(titlesDraft({ originalTitle: 'A', romajiTitle: null })).toEqual({ originalTitle: 'A', romajiTitle: '', englishTitle: '' });
    expect(titlesDraft(null)).toEqual({ originalTitle: '', romajiTitle: '', englishTitle: '' });
  });

  it('treats missing and null optional titles as equal', () => {
    expect(sameTitles({ originalTitle: 'A', romajiTitle: null }, { originalTitle: 'A', englishTitle: '' })).toBe(true);
    expect(sameTitles({ originalTitle: 'A', englishTitle: 'B' }, { originalTitle: 'A' })).toBe(false);
  });
});

describe('franchiseDisplayName', () => {
  const node = (deckId: number, originalTitle: string, englishTitle: string | null, releaseDate: string) =>
    ({ deckId, originalTitle, romajiTitle: null, englishTitle, releaseDate }) as FranchiseNode;

  it('localises the franchise titles', () => {
    const franchise = { originalTitle: '推しの子', romajiTitle: null, englishTitle: 'Oshi no Ko', nodes: [node(1, 'X', 'Y', '2020-01-01')] };
    expect(franchiseDisplayName(franchise, english)).toBe('Oshi no Ko');
  });

  it('falls back to the earliest entry without franchise titles', () => {
    const franchise = {
      originalTitle: null,
      romajiTitle: null,
      englishTitle: null,
      nodes: [node(2, '後', 'Later', '2021-01-01'), node(1, '先', 'Earlier', '2019-01-01')],
    };
    expect(franchiseDisplayName(franchise, english)).toBe('Earlier');
  });
});
