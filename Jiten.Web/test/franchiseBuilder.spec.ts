import { describe, expect, it } from 'vitest';
import { DeckRelationshipType, MediaType } from '../app/types/enums';
import { SeriesKind } from '../app/types/series';
import type { Franchise } from '../app/types/types';
import { getEdgeFlow } from '../app/utils/relationshipRoles';
import {
  applyLink,
  buildSaveRequest,
  builderChecks,
  builderStateFromFranchise,
  cardRemoveAction,
  chainSequels,
  checkLink,
  choiceForEdge,
  choiceToEdge,
  cutOffBy,
  discardPending,
  dropLink,
  filterLinkChoices,
  hasLinks,
  hasMembership,
  joinGroup,
  layoutBoard,
  leaveGroup,
  lineInSeries,
  lineOf,
  linkChoiceByKey,
  linkChoices,
  linkSentence,
  otherSeriesOfLine,
  pendingChanges,
  putLinesInSeries,
  revertLink,
  toBuilderDeck,
  type BuilderDeck,
  type BuilderSeries,
  type BuilderState,
} from '../app/utils/franchiseBuilder';
import { parseBuilderCommand, resolveDeckName, type CommandContext } from '../app/utils/franchiseBuilderCommand';

const { Sequel, SideStory, Adaptation, Alternative, Fandisc, Spinoff } = DeckRelationshipType;
const choice = (key: string) => linkChoiceByKey(key)!;
const name = (id: number) => `D${id}`;
const edge = (sourceDeckId: number, targetDeckId: number, relationshipType: DeckRelationshipType) => ({ sourceDeckId, targetDeckId, relationshipType });

const stateWith = (edges: ReturnType<typeof edge>[], board = [1, 2, 3, 4, 5]): BuilderState => ({
  board: [...board],
  edges: edges.map((e, i) => ({ ...e, id: i + 1, status: 'saved' as const })),
  members: [],
});

describe('getEdgeFlow', () => {
  it('reads sequel-like edges as target before source', () => {
    for (const type of [Sequel, Fandisc, Spinoff, SideStory]) expect(getEdgeFlow(edge(2, 1, type))).toEqual({ from: 1, to: 2, directed: true });
  });

  it('reads adaptation edges as source before target', () => {
    expect(getEdgeFlow(edge(1, 2, Adaptation))).toEqual({ from: 1, to: 2, directed: true });
  });

  it('treats alternative as undirected', () => {
    expect(getEdgeFlow(edge(1, 2, Alternative)).directed).toBe(false);
  });
});

describe('choiceToEdge', () => {
  it('stores "2 is a sequel to 1" as 2 -> 1 Sequel', () => {
    expect(choiceToEdge(choice('sequel'), 2, 1)).toEqual(edge(2, 1, Sequel));
  });

  it('stores "1 is a prequel to 2" the same way', () => {
    expect(choiceToEdge(choice('prequel'), 1, 2)).toEqual(edge(2, 1, Sequel));
  });

  it('stores "2 is an adaptation of 1" as 1 -> 2 Adaptation', () => {
    expect(choiceToEdge(choice('adaptation'), 2, 1)).toEqual(edge(1, 2, Adaptation));
  });

  it('puts the subject later in story order for every directed choice', () => {
    for (const c of linkChoices.filter((x) => x.key !== 'alternative' && x.key !== 'prequel')) {
      const flow = getEdgeFlow(choiceToEdge(c, 2, 1));
      expect(flow).toEqual({ from: 1, to: 2, directed: true });
    }
  });

  it('stores every choice the way the role table did', () => {
    expect(choiceToEdge(choice('sidestory'), 2, 1)).toEqual(edge(2, 1, SideStory));
    expect(choiceToEdge(choice('spinoff'), 2, 1)).toEqual(edge(2, 1, Spinoff));
    expect(choiceToEdge(choice('fandisc'), 2, 1)).toEqual(edge(2, 1, Fandisc));
    expect(choiceToEdge(choice('alternative'), 2, 1)).toEqual(edge(1, 2, Alternative));
  });

  it('round-trips through choiceForEdge', () => {
    for (const c of linkChoices) {
      const e = choiceToEdge(c, 5, 9);
      const back = choiceForEdge(e)!;
      expect(choiceToEdge(back.choice, back.subject, back.object)).toEqual(e);
    }
  });

  it('phrases a link with the later work first', () => {
    expect(linkSentence(edge(1, 2, Adaptation))).toEqual({ first: 2, verb: 'is an adaptation of', second: 1 });
    expect(linkSentence(edge(2, 1, Sequel))).toEqual({ first: 2, verb: 'is a sequel to', second: 1 });
  });
});

describe('filterLinkChoices', () => {
  it('matches labels and terms by prefix', () => {
    expect(filterLinkChoices('pre').map((c) => c.key)).toEqual(['prequel']);
    expect(filterLinkChoices('remake').map((c) => c.key)).toEqual(['alternative']);
    expect(filterLinkChoices('')).toHaveLength(linkChoices.length);
  });
});

describe('checkLink', () => {
  it('rejects a self link', () => {
    expect(checkLink(stateWith([]), edge(1, 1, Sequel), name).error).toMatch(/itself/);
  });

  it('rejects the same link twice', () => {
    expect(checkLink(stateWith([edge(2, 1, Sequel)]), edge(2, 1, Sequel), name).error).toMatch(/already linked/);
  });

  it('rejects an undirected duplicate in either direction', () => {
    expect(checkLink(stateWith([edge(1, 2, Alternative)]), edge(2, 1, Alternative), name).error).toMatch(/already linked/);
  });

  it('offers to replace another link between the same pair', () => {
    const state = stateWith([edge(2, 1, Sequel)]);
    const res = checkLink(state, edge(2, 1, SideStory), name);
    expect(res.error).toBeUndefined();
    expect(res.replaces?.id).toBe(1);
  });

  it('lets a reversed sequel replace the old one instead of reporting a loop', () => {
    const res = checkLink(stateWith([edge(2, 1, Sequel)]), edge(1, 2, Sequel), name);
    expect(res.error).toBeUndefined();
    expect(res.replaces?.id).toBe(1);
  });

  it('rejects a loop through a chain of sequels', () => {
    const state = stateWith([edge(2, 1, Sequel), edge(3, 2, Sequel)]);
    const res = checkLink(state, edge(1, 3, Sequel), name);
    expect(res.error).toMatch(/loop: D1 → D2 → D3/);
  });

  it('finds loops across directed types, following adaptation the stored way round', () => {
    // 1 -> 2 adaptation (2 adapts 1), then 3 is a sequel to 2: 1 before 3. A side story 1 of 3 closes a loop.
    const state = stateWith([edge(1, 2, Adaptation), edge(3, 2, Sequel)]);
    expect(checkLink(state, edge(1, 3, SideStory), name).error).toMatch(/loop/);
    expect(checkLink(state, edge(4, 3, SideStory), name).error).toBeUndefined();
  });

  it('ignores removed links and the edited link', () => {
    const state = stateWith([edge(2, 1, Sequel), edge(3, 2, Sequel)]);
    state.edges[1]!.status = 'removed';
    expect(checkLink(state, edge(1, 3, Sequel), name).error).toBeUndefined();
    expect(checkLink(stateWith([edge(2, 1, Sequel), edge(3, 2, Sequel)]), edge(1, 3, Sequel), name, 2).error).toBeUndefined();
  });
});

describe('pending state', () => {
  it('adds a new link and puts its decks on the board', () => {
    const state = stateWith([], [1]);
    applyLink(state, edge(7, 1, Sequel));
    expect(state.board).toEqual([1, 7]);
    expect(state.edges).toEqual([{ ...edge(7, 1, Sequel), id: 1, status: 'new' }]);
  });

  it('marks a saved link removed and deletes a new one outright', () => {
    const state = stateWith([edge(2, 1, Sequel)]);
    applyLink(state, edge(3, 2, Sequel));
    dropLink(state, 1);
    dropLink(state, 2);
    expect(state.edges).toEqual([{ ...edge(2, 1, Sequel), id: 1, status: 'removed' }]);
  });

  it('restores a removed twin instead of adding it again', () => {
    const state = stateWith([edge(2, 1, Sequel)]);
    dropLink(state, 1);
    applyLink(state, edge(2, 1, Sequel));
    expect(state.edges).toEqual([{ ...edge(2, 1, Sequel), id: 1, status: 'saved' }]);
    expect(pendingChanges(state)).toEqual([]);
  });

  it('replaces the given link', () => {
    const state = stateWith([edge(2, 1, Sequel)]);
    const res = checkLink(state, edge(2, 1, SideStory), name);
    applyLink(state, edge(2, 1, SideStory), res.replaces);
    expect(state.edges.map((e) => [e.relationshipType, e.status])).toEqual([
      [Sequel, 'removed'],
      [SideStory, 'new'],
    ]);
  });

  it('tracks membership joins and leaves', () => {
    const state = stateWith([]);
    state.members.push({ seriesId: 10, deckId: 1, status: 'saved' });
    joinGroup(state, 10, 2);
    joinGroup(state, 10, 2);
    leaveGroup(state, 10, 1);
    expect(state.members).toEqual([
      { seriesId: 10, deckId: 1, status: 'removed' },
      { seriesId: 10, deckId: 2, status: 'new' },
    ]);
    joinGroup(state, 10, 1);
    leaveGroup(state, 10, 2);
    expect(state.members).toEqual([{ seriesId: 10, deckId: 1, status: 'saved' }]);
  });

  it('builds the save request from the pending diff', () => {
    const state = stateWith([edge(2, 1, Sequel), edge(3, 2, Sequel)]);
    dropLink(state, 1);
    applyLink(state, edge(4, 3, Fandisc));
    state.members.push({ seriesId: 10, deckId: 5, status: 'saved' });
    joinGroup(state, 10, 1);
    leaveGroup(state, 10, 5);
    expect(buildSaveRequest(1, state)).toEqual({
      anchorDeckId: 1,
      addEdges: [edge(4, 3, Fandisc)],
      removeEdges: [edge(2, 1, Sequel)],
      addMembers: [{ seriesId: 10, deckId: 1 }],
      removeMembers: [{ seriesId: 10, deckId: 5 }],
    });
  });

  it('reverts single changes and discards everything', () => {
    const state = stateWith([edge(2, 1, Sequel)]);
    dropLink(state, 1);
    applyLink(state, edge(3, 2, Sequel));
    revertLink(state, 1);
    expect(state.edges.map((e) => e.status)).toEqual(['saved', 'new']);
    joinGroup(state, 10, 1);
    discardPending(state);
    expect(pendingChanges(state)).toEqual([]);
    expect(state.edges).toHaveLength(1);
  });
});

describe('putLinesInSeries', () => {
  const groups: BuilderSeries[] = [
    { seriesId: 10, name: 'A', kind: SeriesKind.Series },
    { seriesId: 11, name: 'B', kind: SeriesKind.Series },
    { seriesId: 20, name: 'World', kind: SeriesKind.Setting },
  ];
  const withMembers = (members: [number, number][]): BuilderState => ({
    ...stateWith([edge(2, 1, Sequel)]),
    members: members.map(([seriesId, deckId]) => ({ seriesId, deckId, status: 'saved' as const })),
  });
  const asGiven = (ids: number[]) => ids;

  it('moves the whole line out of its old series and keeps settings', () => {
    const state = withMembers([
      [10, 1],
      [20, 1],
    ]);
    expect(putLinesInSeries(state, groups, [2], 11, asGiven)).toEqual([2]);
    expect(state.members).toEqual([
      { seriesId: 10, deckId: 1, status: 'removed' },
      { seriesId: 20, deckId: 1, status: 'saved' },
      { seriesId: 11, deckId: 2, status: 'new' },
    ]);
  });

  it('drops an unsaved membership instead of marking it removed', () => {
    const state = withMembers([]);
    joinGroup(state, 10, 3);
    putLinesInSeries(state, groups, [3], 11, asGiven);
    expect(state.members).toEqual([{ seriesId: 11, deckId: 3, status: 'new' }]);
  });

  it('adds a line that was in no series', () => {
    const state = withMembers([]);
    putLinesInSeries(state, groups, [4], 10, asGiven);
    expect(state.members).toEqual([{ seriesId: 10, deckId: 4, status: 'new' }]);
  });

  it('takes one entry per line, the first by the given order, and skips lines already in', () => {
    const state = withMembers([[10, 3]]);
    const joined = putLinesInSeries(state, groups, [2, 1, 3, 4], 10, (ids) => [...ids].sort((a, b) => a - b));
    expect(joined).toEqual([1, 4]);
    expect(state.members.filter((m) => m.status === 'new').map((m) => m.deckId)).toEqual([1, 4]);
  });

  it('joins a setting deck by deck without touching series', () => {
    const state = withMembers([
      [10, 1],
      [20, 3],
    ]);
    expect(putLinesInSeries(state, groups, [2, 3], 20, asGiven)).toEqual([2]);
    expect(state.members).toEqual([
      { seriesId: 10, deckId: 1, status: 'saved' },
      { seriesId: 20, deckId: 3, status: 'saved' },
      { seriesId: 20, deckId: 2, status: 'new' },
    ]);
  });
});

describe('line helpers', () => {
  const groups: BuilderSeries[] = [
    { seriesId: 10, name: 'A', kind: SeriesKind.Series },
    { seriesId: 11, name: 'B', kind: SeriesKind.Series },
    { seriesId: 20, name: 'World', kind: SeriesKind.Setting },
  ];

  it('finds the line of a deck, its series entries and the other series it sits in', () => {
    const state = stateWith([edge(2, 1, Sequel), edge(3, 2, Sequel)]);
    state.members.push(
      { seriesId: 10, deckId: 1, status: 'saved' },
      { seriesId: 11, deckId: 3, status: 'saved' },
      { seriesId: 20, deckId: 2, status: 'saved' }
    );
    expect(lineOf(state, 2).sort()).toEqual([1, 2, 3]);
    expect(lineOf(state, 5)).toEqual([5]);
    expect(lineInSeries(state, 10, 3)).toEqual([1]);
    expect(otherSeriesOfLine(state, groups, 10, 2)).toEqual([11]);
  });

  it('ignores removed links and memberships', () => {
    const state = stateWith([edge(2, 1, Sequel)]);
    state.members.push({ seriesId: 10, deckId: 4, status: 'removed' });
    expect(hasLinks(state, 1)).toBe(true);
    dropLink(state, 1);
    expect(hasLinks(state, 1)).toBe(false);
    expect(lineOf(state, 1)).toEqual([1]);
    expect(hasMembership(state, 4)).toBe(false);
  });

  it('chains sequels in the given order and skips pairs already linked', () => {
    const state = stateWith([edge(2, 1, SideStory)]);
    expect(chainSequels(state, [1, 2, 3])).toEqual({ made: 1, skipped: [[1, 2]] });
    expect(state.edges.at(-1)).toMatchObject(edge(3, 2, Sequel));
  });

  it('names the smaller part a link cuts off, unless a series keeps it joined', () => {
    const state = stateWith([edge(2, 1, Sequel), edge(3, 2, Sequel)], [1, 2, 3]);
    expect(cutOffBy(state, groups, 2)).toEqual([3]);
    state.members.push({ seriesId: 10, deckId: 1, status: 'saved' }, { seriesId: 10, deckId: 3, status: 'saved' });
    expect(cutOffBy(state, groups, 2)).toEqual([]);
    dropLink(state, 1);
    expect(cutOffBy(state, groups, 1)).toEqual([]);
  });
});

describe('builderStateFromFranchise', () => {
  it('drops legacy same-series edges and loads memberships', () => {
    const node = (deckId: number) => ({ deckId }) as Franchise['nodes'][number];
    const franchise = {
      franchiseId: 1,
      name: 'S',
      nameIsManual: false,
      nodes: [node(1), node(2), node(3)],
      edges: [edge(2, 1, Sequel), edge(1, 3, DeckRelationshipType.SameSeries)],
      lines: [],
      series: [{ seriesId: 10, name: 'S', memberDeckIds: [1] }],
      settings: [{ seriesId: 20, name: 'World', memberDeckIds: [3], outside: [], outsideCount: 0 }],
      preferredView: 'timeline',
    } as Franchise;
    const state = builderStateFromFranchise(franchise);
    expect(state.edges).toEqual([{ ...edge(2, 1, Sequel), id: 1, status: 'saved' }]);
    expect(state.members).toEqual([
      { seriesId: 10, deckId: 1, status: 'saved' },
      { seriesId: 20, deckId: 3, status: 'saved' },
    ]);
  });
});

describe('builderChecks', () => {
  const series: BuilderSeries[] = [
    { seriesId: 10, name: 'Main', kind: SeriesKind.Series },
    { seriesId: 11, name: 'Other', kind: SeriesKind.Series },
    { seriesId: 20, name: 'World', kind: SeriesKind.Setting },
  ];
  const year = (id: number) => 2000 + id;

  it('lists lines outside the series and unlinked decks', () => {
    const state = stateWith([edge(2, 1, Sequel), edge(4, 3, Sequel)]);
    state.members.push({ seriesId: 10, deckId: 1, status: 'saved' });
    const checks = builderChecks(state, series, year);
    expect(checks).toContainEqual({ kind: 'outside', lines: [[3, 4]] });
    expect(checks).toContainEqual({ kind: 'unlinked', deckIds: [5] });
  });

  it('settings never count as series membership', () => {
    const state = stateWith([edge(2, 1, Sequel)], [1, 2, 3]);
    state.members.push({ seriesId: 10, deckId: 1, status: 'saved' }, { seriesId: 20, deckId: 3, status: 'saved' });
    expect(builderChecks(state, series, year)).toContainEqual({ kind: 'unlinked', deckIds: [3] });
  });

  it('is ok when every line joins the same series', () => {
    const state = stateWith([edge(2, 1, Sequel)], [1, 2, 3]);
    state.members.push({ seriesId: 10, deckId: 1, status: 'saved' }, { seriesId: 10, deckId: 3, status: 'new' });
    expect(builderChecks(state, series, year)[0]).toEqual({ kind: 'ok', deckCount: 3 });
  });

  it('keeps lines in different series apart', () => {
    const state = stateWith([edge(2, 1, Sequel)], [1, 2, 3]);
    state.members.push({ seriesId: 10, deckId: 1, status: 'saved' }, { seriesId: 11, deckId: 3, status: 'new' });
    expect(builderChecks(state, series, year).some((c) => c.kind === 'ok')).toBe(false);
  });

  it('flags branching sequels and sequels released earlier', () => {
    const state = stateWith([edge(2, 1, Sequel), edge(3, 1, Sequel), edge(1, 5, Sequel)]);
    const checks = builderChecks(state, series, year);
    expect(checks).toContainEqual({ kind: 'branch', deckId: 1, sequels: [2, 3] });
    expect(checks.filter((c) => c.kind === 'order')).toHaveLength(1);
  });
});

describe('layoutBoard', () => {
  const series: BuilderSeries[] = [
    { seriesId: 10, name: 'Main', kind: SeriesKind.Series },
    { seriesId: 11, name: 'Other', kind: SeriesKind.Series },
  ];
  const base = { year: (id: number) => 2000 + id, title: name, compareRelease: (a: number, b: number) => a - b, width: 1200, series };

  it('places a sequel to the right of its prequel, and an adaptation right of its source', () => {
    const layout = layoutBoard({ ...base, board: [1, 2, 3], edges: [edge(2, 1, Sequel), edge(1, 3, Adaptation)], members: [] });
    expect(layout.cards.get(2)!.x).toBeGreaterThan(layout.cards.get(1)!.x);
    expect(layout.cards.get(3)!.x).toBeGreaterThan(layout.cards.get(1)!.x);
    expect(layout.bands.map((b) => b.kind)).toEqual(['empty', 'empty', 'outside']);
  });

  it('lists a line spanning two series under the earlier one', () => {
    const layout = layoutBoard({
      ...base,
      board: [1, 2, 3, 4],
      edges: [edge(2, 1, Sequel)],
      members: [
        { seriesId: 10, deckId: 1 },
        { seriesId: 11, deckId: 2 },
        { seriesId: 10, deckId: 3 },
      ],
    });
    expect(layout.sections.map((s) => s.seriesId)).toEqual([10, 11]);
    expect(layout.sections[0]!.deckCount).toBe(3);
    expect(layout.sections[1]!.deckCount).toBe(0);
    expect(layout.sections[1]!.x).toBe(layout.sections[0]!.x);
    expect(layout.bands.at(-1)).toMatchObject({ kind: 'unlinked', ids: [4] });
    expect([...layout.seriesOf]).toEqual([
      [1, 10],
      [2, 10],
      [3, 10],
    ]);
  });

  it('places line cards by the shared story-line layout', () => {
    const layout = layoutBoard({ ...base, board: [1, 2, 3], edges: [edge(2, 1, Sequel), edge(3, 1, Sequel)], members: [] });
    const at = (id: number) => layout.cards.get(id)!;
    expect(at(2).x).toBe(at(3).x);
    expect(at(2).y).toBeLessThan(at(3).y);
  });
});

describe('cardRemoveAction', () => {
  const series: BuilderSeries[] = [
    { seriesId: 10, name: 'Main', kind: SeriesKind.Series },
    { seriesId: 20, name: 'World', kind: SeriesKind.Setting },
  ];
  const layoutOf = (state: BuilderState) =>
    layoutBoard({
      board: state.board,
      edges: state.edges.filter((e) => e.status !== 'removed'),
      members: state.members.filter((m) => m.status !== 'removed'),
      series,
      year: () => null,
      title: name,
      compareRelease: (a, b) => a - b,
      width: 1200,
    });

  it('takes a card out of the series its line is shown in', () => {
    const state = stateWith([edge(2, 1, Sequel)], [1, 2]);
    state.members.push({ seriesId: 10, deckId: 1, status: 'saved' });
    expect(cardRemoveAction(state, layoutOf(state), 2)).toEqual({ kind: 'leave', seriesId: 10 });
  });

  it('takes a free card off the board', () => {
    const state = stateWith([], [1]);
    expect(cardRemoveAction(state, layoutOf(state), 1)).toEqual({ kind: 'remove' });
  });

  it('offers nothing for a linked card or one in a setting', () => {
    const state = stateWith([edge(2, 1, Sequel)], [1, 2, 3]);
    state.members.push({ seriesId: 20, deckId: 3, status: 'saved' });
    const layout = layoutOf(state);
    expect(cardRemoveAction(state, layout, 1)).toBeNull();
    expect(cardRemoveAction(state, layout, 3)).toBeNull();
  });
});

describe('toBuilderDeck', () => {
  it('reads the year from a release date and leaves it unknown without one', () => {
    const base = { deckId: 1, originalTitle: 'x', mediaType: MediaType.Anime, coverName: '' };
    expect(toBuilderDeck({ ...base, releaseDate: '2004-03-01' })).toMatchObject({ year: 2004, releaseDate: '2004-03-01' });
    expect(toBuilderDeck({ ...base, releaseDate: '0001-01-01' }).year).toBeNull();
    expect(toBuilderDeck(base)).toMatchObject({ year: null, releaseDate: null });
  });
});

describe('parseBuilderCommand', () => {
  const decks: BuilderDeck[] = [
    { deckId: 1, originalTitle: 'ファイナルファンタジーX', englishTitle: 'Final Fantasy X', mediaType: MediaType.VideoGame, year: 2001 },
    { deckId: 2, originalTitle: 'ファイナルファンタジーX-2', englishTitle: 'Final Fantasy X-2', mediaType: MediaType.VideoGame, year: 2003 },
    { deckId: 3, originalTitle: 'ファイナルファンタジーVII', englishTitle: 'Final Fantasy VII', mediaType: MediaType.VideoGame, year: 1997 },
    { deckId: 4, originalTitle: 'ファイナルファンタジーVIII', englishTitle: 'Final Fantasy VIII', mediaType: MediaType.VideoGame, year: 1999 },
    { deckId: 5, originalTitle: 'クライシス コア', englishTitle: 'Crisis Core: Final Fantasy VII', mediaType: MediaType.VideoGame, year: 2007 },
  ];
  const ctx: CommandContext = {
    decks,
    board: new Set([1, 2, 3, 4, 5]),
    name,
  };

  it('returns null for empty input', () => {
    expect(parseBuilderCommand('  ', ctx)).toBeNull();
  });

  it('parses a sequel sentence and stores the sequel as the source', () => {
    expect(parseBuilderCommand('x-2 sequel to x', ctx)).toEqual([{ kind: 'link', subject: 2, object: 1, choice: choice('sequel') }]);
    expect(choiceToEdge(choice('sequel'), 2, 1)).toEqual(edge(2, 1, Sequel));
  });

  it('parses a prequel with numerals and subtitles', () => {
    expect(parseBuilderCommand('crisis core is a prequel to 7', ctx)).toEqual([{ kind: 'link', subject: 5, object: 3, choice: choice('prequel') }]);
  });

  it('does not let vii match viii', () => {
    expect(resolveDeckName('vii', ctx)).toEqual({ id: 3 });
    expect(resolveDeckName('viii', ctx)).toEqual({ id: 4 });
  });

  it('parses chains as sequels', () => {
    expect(parseBuilderCommand('x > x-2', ctx)).toEqual([{ kind: 'link', subject: 2, object: 1, choice: choice('sequel') }]);
  });

  it('resolves Japanese titles and #ids', () => {
    expect(resolveDeckName('クライシス', ctx)).toEqual({ id: 5 });
    expect(resolveDeckName('#4', ctx)).toEqual({ id: 4 });
    expect(resolveDeckName('#99', ctx)).toHaveProperty('error');
  });

  it('reports a name that is too vague', () => {
    expect(resolveDeckName('final fantasy', ctx)).toHaveProperty('error');
  });

  it('reads every phrase of the link choices', () => {
    expect(parseBuilderCommand('x-2 gaiden of x', ctx)).toEqual([{ kind: 'link', subject: 2, object: 1, choice: choice('sidestory') }]);
    expect(parseBuilderCommand('x-2 is a side-story of x', ctx)).toEqual([{ kind: 'link', subject: 2, object: 1, choice: choice('sidestory') }]);
    expect(parseBuilderCommand('viii remake of vii', ctx)).toEqual([{ kind: 'link', subject: 4, object: 3, choice: choice('alternative') }]);
    expect(parseBuilderCommand('crisis core is a spin-off of vii', ctx)).toEqual([{ kind: 'link', subject: 5, object: 3, choice: choice('spinoff') }]);
  });

  it('no longer reads series commands', () => {
    expect(parseBuilderCommand('series x, viii', ctx)![0]).toMatchObject({ kind: 'error' });
  });

  it('explains an unknown deck', () => {
    expect(parseBuilderCommand('zelda sequel to x', ctx)).toEqual([{ kind: 'error', message: 'No deck matches “zelda”.', lookup: 'zelda' }]);
    expect(parseBuilderCommand('#999 sequel to x', ctx)).toEqual([{ kind: 'error', message: 'Looking up deck #999…', lookup: '#999' }]);
  });
});
