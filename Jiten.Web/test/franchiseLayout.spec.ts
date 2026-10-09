import { describe, expect, it } from 'vitest';
import {
  buildFranchiseSeriesLayout,
  compareFranchiseRelease,
  franchiseDerivedChips,
  franchiseDerivedLabel,
  franchiseEntryCountLabel,
  franchiseFirstNode,
  franchiseHasSideLinks,
  franchisePopoverMemberships,
  franchiseUsableEdges,
  layoutStoryLine,
  releaseYearOf,
  resolveFranchiseView,
  horizontalConnector,
  storyLineOrder,
  verticalConnector,
} from '../app/utils/franchiseLayout';
import { linkTypeInfo, relatedMediaLabelFrom } from '../app/utils/relationshipRoles';
import { DeckRelationshipType, MediaType } from '../app/types/enums';
import type { FranchiseEdge, FranchiseNode } from '../app/types/types';
import type { FranchiseLine } from '../app/types/mediaGroup';
import type { FranchiseSeries } from '../app/types/series';

function node(deckId: number, year: number | null, mediaType = MediaType.Anime): FranchiseNode {
  return {
    deckId,
    originalTitle: `deck ${deckId}`,
    romajiTitle: '',
    englishTitle: '',
    coverName: '',
    mediaType,
    releaseDate: year == null ? '0001-01-01' : `${year}-06-01`,
    difficulty: 2,
    difficultyRaw: 2,
    coverage: 0,
    uniqueCoverage: 0,
  };
}

function edge(sourceDeckId: number, targetDeckId: number, relationshipType: DeckRelationshipType): FranchiseEdge {
  return { sourceDeckId, targetDeckId, relationshipType };
}

const { Sequel, Alternative, SideStory, Adaptation, SameSeries, SameSetting } = DeckRelationshipType;

const releaseOrder = (byId: Map<number, FranchiseNode>) => (a: number, b: number) => compareFranchiseRelease(byId.get(a)!, byId.get(b)!);

const lines = (...groups: number[][]): FranchiseLine[] => groups.map((deckIds) => ({ anchorDeckId: deckIds[0]!, deckIds }));

describe('releaseYearOf', () => {
  it('reads the year in UTC and treats the DateOnly default as unknown', () => {
    expect(releaseYearOf('2001-01-01')).toBe(2001);
    expect(releaseYearOf('2001-01-01T00:00:00Z')).toBe(2001);
    expect(releaseYearOf('0001-01-01')).toBeNull();
    expect(releaseYearOf('')).toBeNull();
    expect(releaseYearOf('not a date')).toBeNull();
  });
});

describe('franchiseFirstNode', () => {
  it('picks the earliest release, unknown dates last, lowest id on ties', () => {
    expect(franchiseFirstNode([node(3, null), node(2, 2005), node(1, 2005)])?.deckId).toBe(1);
    expect(franchiseFirstNode([node(4, null)])?.deckId).toBe(4);
    expect(franchiseFirstNode([])).toBeNull();
  });
});

describe('edge labels', () => {
  it('names links and derived works from the shared relationship table', () => {
    expect(linkTypeInfo(SideStory).label).toBe('side story');
    expect(franchiseDerivedLabel(Adaptation)).toBe('Adaptation of');
    expect(franchiseDerivedLabel(DeckRelationshipType.Spinoff)).toBe('Spin-off of');
    expect(franchiseDerivedLabel(Sequel)).toBeNull();
    expect(franchiseDerivedLabel(Alternative)).toBeNull();
  });
});

describe('relatedMediaLabelFrom', () => {
  it('names the other deck from either end of a stored edge', () => {
    const sequel = edge(2, 1, Sequel);
    expect(relatedMediaLabelFrom(sequel, 2)).toBe('Prequel');
    expect(relatedMediaLabelFrom(sequel, 1)).toBe('Sequel');
    const adaptation = edge(1, 3, Adaptation);
    expect(relatedMediaLabelFrom(adaptation, 1)).toBe('Adaptation');
    expect(relatedMediaLabelFrom(adaptation, 3)).toBe('Source');
  });
});

describe('connectors', () => {
  const box = (left: number, top: number) => ({ left, top, right: left + 100, bottom: top + 50 });

  it('curves from the bottom of the upper box to the top of the lower one', () => {
    const c = verticalConnector(box(0, 0), box(0, 150));
    expect(c.path).toBe('M 50 50 C 50 100, 50 100, 50 150');
    expect(c.mid).toEqual({ x: 50, y: 100 });
    expect(verticalConnector(box(0, 150), box(0, 0)).path).toBe('M 50 150 C 50 100, 50 100, 50 50');
  });

  it('keeps a minimum bend on short hops', () => {
    expect(verticalConnector(box(0, 0), box(0, 60), 40).path).toBe('M 50 50 C 50 90, 50 20, 50 60');
    expect(horizontalConnector(box(0, 0), box(110, 0), 24).path).toBe('M 100 25 C 124 25, 86 25, 110 25');
  });

  it('joins facing sides left to right or right to left', () => {
    expect(horizontalConnector(box(0, 0), box(300, 0)).path).toBe('M 100 25 C 200 25, 200 25, 300 25');
    expect(horizontalConnector(box(300, 0), box(0, 0)).path).toBe('M 300 25 C 200 25, 200 25, 100 25');
  });
});

describe('franchiseUsableEdges', () => {
  it('drops legacy series and setting links and edges to missing decks', () => {
    const nodes = [node(1, 2000), node(2, 2001)];
    const edges = [edge(2, 1, Sequel), edge(1, 2, SameSeries), edge(1, 2, SameSetting), edge(1, 99, Adaptation)];
    expect(franchiseUsableEdges(nodes, edges)).toEqual([edge(2, 1, Sequel)]);
  });
});

describe('layoutStoryLine', () => {
  it('places sequels to the right and alternatives level with their counterpart', () => {
    const nodes = [node(5, 1997), node(6, 2007), node(7, 2005), node(9, 2020), node(10, 2024)];
    const byId = new Map(nodes.map((n) => [n.deckId, n]));
    const edges = [edge(5, 6, Sequel), edge(7, 5, Sequel), edge(5, 9, Alternative), edge(10, 9, Sequel)];
    const layout = layoutStoryLine([5, 6, 7, 9, 10], edges, releaseOrder(byId));
    const layer = (id: number) => layout.pos.get(id)!.layer;
    expect(layer(6)).toBe(0);
    expect(layer(5)).toBe(1);
    expect(layer(7)).toBe(2);
    expect(layer(9)).toBe(1);
    expect(layer(10)).toBe(2);
    expect(layout.pos.get(5)!.slot).not.toBe(layout.pos.get(9)!.slot);
    expect(storyLineOrder(layout)[0]).toBe(6);
  });

  it('terminates on a directed cycle', () => {
    const nodes = [node(1, 2000), node(2, 2001)];
    const byId = new Map(nodes.map((n) => [n.deckId, n]));
    const layout = layoutStoryLine([1, 2], [edge(1, 2, Sequel), edge(2, 1, Sequel)], releaseOrder(byId));
    expect(layout.layers).toBeLessThanOrEqual(2);
  });

  it('puts an adaptation right of its source and a remake level with the work it retells', () => {
    const edges = [edge(1, 2, Adaptation), edge(3, 1, Sequel), edge(4, 3, Alternative), edge(5, 4, Sequel)];
    const layout = layoutStoryLine([1, 2, 3, 4, 5], edges, (a, b) => a - b);
    const layer = (id: number) => layout.pos.get(id)!.layer;
    expect([layer(1), layer(2), layer(3)]).toEqual([0, 1, 1]);
    expect(layer(4)).toBe(layer(3));
    expect(layer(5)).toBe(layer(4) + 1);
  });

  it('places a deck linked only by an alternative level with its latest linked neighbour', () => {
    const edges = [edge(2, 1, Sequel), edge(3, 1, Alternative), edge(3, 2, Alternative)];
    const layout = layoutStoryLine([1, 2, 3], edges, (a, b) => a - b);
    expect(layout.pos.get(3)!.layer).toBe(1);
  });

  it('breaks slot ties with the comparator', () => {
    const edges = [edge(2, 1, Sequel), edge(3, 1, Sequel)];
    const slot = (compare: (a: number, b: number) => number) => layoutStoryLine([1, 2, 3], edges, compare).pos.get(3)!.slot;
    expect(slot((a, b) => a - b)).toBe(1);
    expect(slot((a, b) => b - a)).toBe(0);
  });
});

describe('buildFranchiseSeriesLayout', () => {
  const series = (seriesId: number, originalTitle: string, memberDeckIds: number[]): FranchiseSeries => ({ seriesId, originalTitle, memberDeckIds });

  it('builds release-ordered rows and merges consecutive standalone entries', () => {
    const nodes = [node(1, 1987), node(27, 1988), node(28, 1990), node(2, 1991), node(3, 2008), node(4, 1994)];
    const layout = buildFranchiseSeriesLayout({ nodes, lines: lines([3, 2]), series: [series(100, 'FF', [1, 27, 28, 2, 4])] });
    const rows = layout.groups[0]!.rows.map((r) => [r.kind, r.deckIds]);
    expect(rows).toEqual([
      ['standalone', [1, 27, 28]],
      ['line', [2, 3]],
      ['standalone', [4]],
    ]);
    expect(layout.entryCount).toBe(5);
    expect(layout.deckSeries.get(3)).toEqual([{ seriesId: 100, viaDeckId: 2 }]);
    expect(layout.deckSeries.get(2)).toEqual([{ seriesId: 100, viaDeckId: null }]);
  });

  it('orders series by their first entry, skips empty ones and leaves non-series lines trailing', () => {
    const nodes = [node(1, 1987), node(5, 1997), node(6, 2007), node(11, 2001), node(50, 2010), node(51, 2012)];
    const layout = buildFranchiseSeriesLayout({
      nodes,
      lines: lines([6, 5], [51, 50]),
      series: [series(101, 'Compilation of FF VII', [5]), series(100, 'FF', [1, 11]), series(102, 'Empty', [])],
    });
    expect(layout.groups.map((g) => g.originalTitle)).toEqual(['FF', 'Compilation of FF VII']);
    expect(layout.groups[0]!.rows.map((r) => r.deckIds)).toEqual([[1, 11]]);
    expect(layout.groups[1]!.rows[0]).toMatchObject({ kind: 'line', deckIds: [5, 6], entryId: 5 });
    expect(layout.groups[1]!.deckIds).toEqual([5, 6]);
    expect(layout.unassigned.map((r) => r.deckIds)).toEqual([[50, 51]]);
    expect(layout.rows.map((r) => r.deckIds)).toEqual([
      [1, 11],
      [5, 6],
      [50, 51],
    ]);
    expect(layout.rowOf.get(6)).toBe(layout.rowOf.get(5));
  });

  it('assigns a line to the lowest series id of its earliest member', () => {
    const nodes = [node(1, 2000), node(2, 2001)];
    const layout = buildFranchiseSeriesLayout({
      nodes,
      lines: lines([2, 1]),
      series: [series(101, 'B', [1]), series(100, 'A', [1])],
    });
    expect(layout.groups.map((g) => g.seriesId)).toEqual([100]);
    expect(layout.deckSeries.get(2)).toEqual([{ seriesId: 100, viaDeckId: 1 }]);
  });
});

describe('resolveFranchiseView', () => {
  const withSeries = { series: [{ seriesId: 1, originalTitle: 'S', memberDeckIds: [1] }], preferredView: 'series' as const };
  const noSeries = { series: [], preferredView: 'timeline' as const };

  it('prefers the query', () => {
    expect(resolveFranchiseView('web', withSeries)).toBe('web');
    expect(resolveFranchiseView('timeline', withSeries)).toBe('timeline');
    expect(resolveFranchiseView('series', withSeries)).toBe('series');
  });

  it('falls back to the preferred view', () => {
    expect(resolveFranchiseView(undefined, withSeries)).toBe('series');
    expect(resolveFranchiseView('bogus', noSeries)).toBe('timeline');
  });

  it('shows Series on request but never defaults to it without a series', () => {
    expect(resolveFranchiseView('series', noSeries)).toBe('series');
    expect(resolveFranchiseView(undefined, { series: [], preferredView: 'series' })).toBe('timeline');
  });
});

describe('stored edge direction', () => {
  it('lays X left of X-2 when X-2 is stored as the Sequel source, arrow pointing at X-2', () => {
    const x = node(11, 2001, MediaType.VideoGame);
    const x2 = node(12, 2003, MediaType.VideoGame);
    const byId = new Map([x, x2].map((n) => [n.deckId, n]));
    const stored = edge(12, 11, Sequel);
    const layout = layoutStoryLine([12, 11], [stored], releaseOrder(byId));
    expect(layout.pos.get(11)!.layer).toBe(0);
    expect(layout.pos.get(12)!.layer).toBe(1);
  });
});

describe('franchiseDerivedChips', () => {
  it('names the earliest original and counts the rest', () => {
    const nodes = [node(1, 2000), node(2, 1990), node(3, 2005)];
    const byId = new Map(nodes.map((n) => [n.deckId, n]));
    const edges = [edge(1, 3, Adaptation), edge(2, 3, Adaptation)];
    expect(franchiseDerivedChips(edges, byId).get(3)).toEqual({ type: Adaptation, originId: 2, extra: 1 });
    expect(franchiseDerivedChips([edge(2, 1, Sequel)], byId).size).toBe(0);
  });

  it('names the stored SideStory target on the card', () => {
    const source = node(1, 2016);
    const side = node(19, 2016);
    const byId = new Map([source, side].map((n) => [n.deckId, n]));
    const chips = franchiseDerivedChips([edge(19, 1, SideStory)], byId);
    expect(chips.get(19)?.originId).toBe(1);
    expect(chips.has(1)).toBe(false);
  });
});

describe('franchisePopoverMemberships', () => {
  const localise = (t: { originalTitle: string; englishTitle?: string | null }) => t.englishTitle ?? t.originalTitle;
  const franchise = {
    series: [{ seriesId: 10, originalTitle: 'Main', memberDeckIds: [1] }],
    settings: [{ seriesId: 20, originalTitle: 'World', memberDeckIds: [1, 2], outside: [], outsideCount: 0 }],
  };

  it('lists direct series and settings by default', () => {
    expect(franchisePopoverMemberships(franchise, 1, localise, () => null).map((m) => [m.kind, m.seriesId, m.name])).toEqual([
      ['series', 10, 'Main'],
      ['setting', 20, 'World'],
    ]);
  });

  it('names the deck a line reaches its series through', () => {
    const rows = franchisePopoverMemberships(franchise, 2, localise, (id) => `Deck ${id}`, [{ seriesId: 10, viaDeckId: 1 }]);
    expect(rows[0]).toMatchObject({ kind: 'series', name: 'Main (through Deck 1)' });
  });

  it('localises series and setting titles', () => {
    const localised = {
      series: [{ seriesId: 10, originalTitle: 'メイン', englishTitle: 'Main', memberDeckIds: [1] }],
      settings: [{ seriesId: 20, originalTitle: '世界', englishTitle: 'World', memberDeckIds: [1], outside: [], outsideCount: 0 }],
    };
    expect(franchisePopoverMemberships(localised, 1, localise).map((m) => m.name)).toEqual(['Main', 'World']);
  });
});

describe('franchiseHasSideLinks', () => {
  it('is false when every link is a sequel or an alternative', () => {
    expect(franchiseHasSideLinks([edge(2, 1, Sequel), edge(1, 3, Alternative)])).toBe(false);
    expect(franchiseHasSideLinks([])).toBe(false);
  });

  it('is true once a side story or adaptation is linked', () => {
    expect(franchiseHasSideLinks([edge(2, 1, Sequel), edge(1, 4, Adaptation)])).toBe(true);
  });
});

describe('franchiseEntryCountLabel', () => {
  it('counts the whole franchise plainly', () => {
    expect(franchiseEntryCountLabel(1, 1)).toBe('1 entry');
    expect(franchiseEntryCountLabel(33, 33)).toBe('33 entries');
  });

  it('names the total when a filter narrows the set', () => {
    expect(franchiseEntryCountLabel(6, 33)).toBe('6 of 33 entries');
  });
});
