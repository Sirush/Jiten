import { describe, expect, it } from 'vitest';
import { MediaType } from '../app/types/enums';
import { MediaGroupKind } from '../app/types/mediaGroup';
import type { Franchise, FranchiseNode } from '../app/types/types';
import {
  activeScopeChip,
  buildScopeChips,
  countByMediaType,
  filterNodesByMediaTypes,
  filterNodesByScope,
  formatScope,
  franchisePath,
  groupFilterSummary,
  mediaGroupLabel,
  mediaGroupLink,
  parseScope,
  scopeDeckIds,
} from '../app/utils/mediaGroup';

const node = (deckId: number, releaseDate: string, mediaType = MediaType.VideoGame): FranchiseNode => ({
  deckId,
  releaseDate,
  mediaType,
  originalTitle: `Deck ${deckId}`,
  romajiTitle: `Romaji ${deckId}`,
  englishTitle: '',
  coverName: 'nocover.jpg',
  difficulty: 2,
  difficultyRaw: 2.4,
  coverage: 0,
  uniqueCoverage: 0,
});

type ScopeFranchise = Pick<Franchise, 'nodes' | 'series' | 'lines'>;

const localise = (t: { originalTitle: string }) => t.originalTitle;

// Final Fantasy shape: one series widening unlinked lines, another naming the FF VII line plus a spin-off.
const franchise: ScopeFranchise = {
  nodes: [
    node(1, '1987-12-18'),
    node(7, '1997-01-31'),
    node(8, '2007-09-13'),
    node(9, '2005-09-14', MediaType.Movie),
    node(12, '2006-03-16'),
    node(13, '2007-03-01'),
    node(20, '2001-07-19'),
  ],
  edges: [],
  series: [
    { seriesId: 100, originalTitle: 'Final Fantasy', memberDeckIds: [1, 12, 20] },
    { seriesId: 101, originalTitle: 'Compilation of FF VII', memberDeckIds: [7, 8, 9] },
  ],
  lines: [
    { anchorDeckId: 1, deckIds: [1] },
    { anchorDeckId: 12, deckIds: [12, 13] },
    { anchorDeckId: 7, deckIds: [7, 8, 9] },
    { anchorDeckId: 20, deckIds: [20] },
  ],
} as ScopeFranchise;

describe('parseScope / formatScope', () => {
  it('reads series and line scopes', () => {
    expect(parseScope('series:12')).toEqual({ kind: MediaGroupKind.Series, id: 12 });
    expect(parseScope(['line:345'])).toEqual({ kind: MediaGroupKind.Line, id: 345 });
  });

  it('treats anything else as the whole franchise', () => {
    for (const value of [undefined, '', 'series:', 'series:0', 'franchise:3', 'line:-1', 'series:1x', 12]) expect(parseScope(value)).toBeNull();
  });

  it('round-trips and omits the whole franchise', () => {
    expect(formatScope({ kind: MediaGroupKind.Line, id: 345 })).toBe('line:345');
    expect(formatScope({ kind: MediaGroupKind.Franchise, id: 3 })).toBeUndefined();
    expect(formatScope(null)).toBeUndefined();
  });

  it('builds franchise paths with and without a scope', () => {
    expect(franchisePath(4)).toBe('/franchise/4');
    expect(franchisePath(4, { kind: MediaGroupKind.Series, id: 12 })).toBe('/franchise/4?scope=series:12');
  });
});

describe('scopeDeckIds', () => {
  it('returns the members of a series that are in the franchise', () => {
    expect(scopeDeckIds(franchise, { kind: MediaGroupKind.Series, id: 100 })).toEqual([1, 12, 20]);
    const partial = { ...franchise, nodes: franchise.nodes.filter((n) => n.deckId !== 12) };
    expect(scopeDeckIds(partial, { kind: MediaGroupKind.Series, id: 100 })).toEqual([1, 20]);
  });

  it('returns a line by any of its decks', () => {
    expect(scopeDeckIds(franchise, { kind: MediaGroupKind.Line, id: 12 })).toEqual([12, 13]);
    expect(scopeDeckIds(franchise, { kind: MediaGroupKind.Line, id: 13 })).toEqual([12, 13]);
  });

  it('returns null for the whole franchise and for groups the franchise does not hold', () => {
    expect(scopeDeckIds(franchise, null)).toBeNull();
    expect(scopeDeckIds(franchise, { kind: MediaGroupKind.Series, id: 999 })).toBeNull();
    expect(scopeDeckIds(franchise, { kind: MediaGroupKind.Line, id: 999 })).toBeNull();
  });
});

describe('buildScopeChips', () => {
  const chips = buildScopeChips(franchise, localise);

  it('lists the whole franchise, series in list order, then lines of 2+ decks by release', () => {
    expect(chips.map((c) => c.key)).toEqual(['all', 'series:100', 'series:101', 'line:12']);
    expect(chips.map((c) => c.label)).toEqual(['Whole franchise', 'Final Fantasy', 'Compilation of FF VII', 'Deck 12']);
    expect(chips[0]!.deckIds).toHaveLength(7);
  });

  it('drops a line covering the same decks as a series', () => {
    expect(chips.some((c) => c.key === 'line:7')).toBe(false);
  });

  it('hides a series spanning the whole franchise', () => {
    const whole = { ...franchise, nodes: franchise.nodes.filter((n) => [1, 12, 20].includes(n.deckId)), lines: [] };
    expect(buildScopeChips(whole, localise).map((c) => c.key)).toEqual(['all']);
  });

  it('skips series with no decks in the franchise', () => {
    const odd: ScopeFranchise = {
      ...franchise,
      lines: [],
      series: [
        { seriesId: 5, originalTitle: 'Empty', memberDeckIds: [] },
        { seriesId: 6, originalTitle: 'Elsewhere', memberDeckIds: [404] },
        { seriesId: 7, originalTitle: 'Kept', memberDeckIds: [12, 13] },
      ],
    };
    expect(buildScopeChips(odd, localise).map((c) => c.key)).toEqual(['all', 'series:7']);
  });
});

describe('activeScopeChip', () => {
  const chips = buildScopeChips(franchise, localise);

  it('marks the whole franchise without a scope', () => {
    expect(activeScopeChip(chips, null, null)?.key).toBe('all');
  });

  it('marks the scope chip, or the chip with the same decks when the scope has none', () => {
    expect(activeScopeChip(chips, { kind: MediaGroupKind.Line, id: 12 }, [12, 13])?.key).toBe('line:12');
    expect(activeScopeChip(chips, { kind: MediaGroupKind.Line, id: 7 }, [9, 8, 7])?.key).toBe('series:101');
  });

  it('marks the line chip for a line named by a later deck', () => {
    const scope = { kind: MediaGroupKind.Line, id: 13 };
    expect(activeScopeChip(chips, scope, scopeDeckIds(franchise, scope))?.key).toBe('line:12');
  });

  it('marks the whole franchise for a series covering every deck', () => {
    const whole = { ...franchise, nodes: franchise.nodes.filter((n) => [1, 12, 20].includes(n.deckId)), lines: [] };
    const scope = { kind: MediaGroupKind.Series, id: 100 };
    expect(activeScopeChip(buildScopeChips(whole, localise), scope, scopeDeckIds(whole, scope))?.key).toBe('all');
  });
});

describe('node filters', () => {
  it('filters by scope and by media type', () => {
    expect(filterNodesByScope(franchise.nodes, [7, 9]).map((n) => n.deckId)).toEqual([7, 9]);
    expect(filterNodesByScope(franchise.nodes, null)).toHaveLength(7);
    expect(filterNodesByMediaTypes(franchise.nodes, [MediaType.Movie]).map((n) => n.deckId)).toEqual([9]);
    expect(filterNodesByMediaTypes(franchise.nodes, [])).toHaveLength(7);
  });

  it('counts decks per media type in first-seen order', () => {
    expect([...countByMediaType(franchise.nodes)]).toEqual([
      [MediaType.VideoGame, 6],
      [MediaType.Movie, 1],
    ]);
  });
});

describe('study deck labels', () => {
  it('names each kind and falls back when the group is gone', () => {
    expect(mediaGroupLabel({ groupKind: MediaGroupKind.Franchise, groupTitles: { originalTitle: 'Final Fantasy' } }, localise)).toBe(
      'Franchise: Final Fantasy'
    );
    expect(mediaGroupLabel({ groupKind: MediaGroupKind.Series, groupTitles: null }, localise)).toBe('Series removed');
    expect(mediaGroupLabel({ groupKind: MediaGroupKind.Line, groupTitles: { originalTitle: 'ファイナルファンタジーXII' } }, localise)).toBe(
      'Series: ファイナルファンタジーXII'
    );
  });

  it('links to the franchise page, scoped for series and lines', () => {
    expect(mediaGroupLink({ groupKind: MediaGroupKind.Franchise, groupId: 3, groupTitles: { originalTitle: 'FF' }, groupFranchiseId: 3 })).toBe('/franchise/3');
    expect(mediaGroupLink({ groupKind: MediaGroupKind.Series, groupId: 101, groupTitles: { originalTitle: 'C' }, groupFranchiseId: 3 })).toBe(
      '/franchise/3?scope=series:101'
    );
    expect(mediaGroupLink({ groupKind: MediaGroupKind.Line, groupId: 12, groupFranchiseId: 3 })).toBe('/franchise/3?scope=line:12');
    expect(mediaGroupLink({ groupKind: MediaGroupKind.Series, groupId: 101, groupTitles: { originalTitle: 'C' }, groupFranchiseId: null })).toBeNull();
    expect(mediaGroupLink({ groupKind: MediaGroupKind.Franchise, groupId: 3, groupTitles: null })).toBeNull();
  });

  it('summarises the filters', () => {
    expect(groupFilterSummary(null, null)).toBe('All media');
    expect(groupFilterSummary([MediaType.Anime, MediaType.Novel], [5])).toBe('Anime, Novels, 1 title skipped');
    expect(groupFilterSummary([], [5, 6])).toBe('All media, 2 titles skipped');
  });
});
