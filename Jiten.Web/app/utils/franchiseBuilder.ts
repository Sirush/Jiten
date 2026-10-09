import { DeckRelationshipType } from '~/types/enums';
import type { MediaType } from '~/types/enums';
import { SeriesKind } from '~/types/series';
import type { Franchise, FranchiseEdge } from '~/types/types';
import { layoutStoryLine, releaseYearOf } from '~/utils/franchiseLayout';
import { getEdgeFlow, isLegacyGroupRelationship, linkTypeInfo, type LinkTone } from '~/utils/relationshipRoles';
import { UnionFind } from '~/utils/unionFind';

export type PendingStatus = 'saved' | 'new' | 'removed';

export interface BuilderEdge extends FranchiseEdge {
  id: number;
  status: PendingStatus;
}

export interface BuilderMembership {
  seriesId: number;
  deckId: number;
  status: PendingStatus;
}

export type FranchiseBuilderMember = Omit<BuilderMembership, 'status'>;

export interface FranchiseBuilderSaveRequest {
  anchorDeckId: number;
  addEdges: FranchiseEdge[];
  removeEdges: FranchiseEdge[];
  addMembers: FranchiseBuilderMember[];
  removeMembers: FranchiseBuilderMember[];
}

export interface BuilderState {
  board: number[];
  edges: BuilderEdge[];
  members: BuilderMembership[];
}

export interface BuilderDeck {
  deckId: number;
  originalTitle: string;
  romajiTitle?: string | null;
  englishTitle?: string | null;
  mediaType: MediaType;
  year: number | null;
  releaseDate?: string | null;
  coverName?: string;
}

type BuilderDeckSource = Pick<BuilderDeck, 'deckId' | 'originalTitle' | 'romajiTitle' | 'englishTitle' | 'mediaType' | 'coverName'> & {
  releaseDate?: string | Date | null;
};

/** Search suggestions carry no release date, so the year stays unknown until the franchise is loaded. */
export function toBuilderDeck(d: BuilderDeckSource): BuilderDeck {
  const releaseDate = d.releaseDate == null ? null : String(d.releaseDate);
  return {
    deckId: d.deckId,
    originalTitle: d.originalTitle,
    romajiTitle: d.romajiTitle,
    englishTitle: d.englishTitle,
    mediaType: d.mediaType,
    year: releaseYearOf(releaseDate),
    releaseDate,
    coverName: d.coverName,
  };
}

export interface BuilderSeries {
  seriesId: number;
  name: string;
  kind: SeriesKind;
  /** Setting members outside this franchise, as last loaded. */
  outsideCount?: number;
}

export interface SeriesDialogRequest {
  mode: 'create' | 'rename';
  kind: SeriesKind;
  seriesId: number | null;
  name: string;
}

/** Text colour per tone; edges and arrowheads draw with currentColor. */
export const linkToneClass: Record<LinkTone, string> = {
  sequel: 'text-purple-700 dark:text-purple-400',
  side: 'text-teal-700 dark:text-teal-400',
  spin: 'text-amber-700 dark:text-amber-400',
  fan: 'text-pink-700 dark:text-pink-400',
  adapt: 'text-blue-700 dark:text-blue-400',
  alt: 'text-gray-500 dark:text-gray-400',
};

/** "first verb second", with the later work as the subject of a directed link. */
export interface LinkSentence {
  first: number;
  verb: string;
  second: number;
}

export function linkSentence(edge: FranchiseEdge): LinkSentence {
  const flow = getEdgeFlow(edge);
  const verb = linkTypeInfo(edge.relationshipType).verb;
  return flow.directed ? { first: flow.to, verb, second: flow.from } : { first: edge.sourceDeckId, verb, second: edge.targetDeckId };
}

export function linkSentenceText(edge: FranchiseEdge, name: (deckId: number) => string): string {
  const s = linkSentence(edge);
  return `${name(s.first)} ${s.verb} ${name(s.second)}`;
}

export interface LinkChoice {
  key: string;
  label: string;
  words: string;
  /** Prefixes the picker filter matches. */
  terms: string[];
  /** Phrases the command bar reads between two deck names. */
  phrases: string[];
  type: DeckRelationshipType;
  /** The subject is the later work in story order; ignored for undirected types. */
  subjectIsLater: boolean;
}

const { Sequel, SideStory, Spinoff, Fandisc, Adaptation, Alternative } = DeckRelationshipType;

export const linkChoices: LinkChoice[] = (
  [
    {
      key: 'sequel',
      label: 'Sequel',
      type: Sequel,
      subjectIsLater: true,
      terms: ['sequel', 'after', 'follows', 'next', 'continues'],
      phrases: ['sequel', 'follows', 'continues', 'after'],
    },
    {
      key: 'prequel',
      label: 'Prequel',
      words: 'is a prequel to',
      type: Sequel,
      subjectIsLater: false,
      terms: ['prequel', 'before', 'precedes'],
      phrases: ['prequel', 'precedes', 'before'],
    },
    {
      key: 'sidestory',
      label: 'Side story',
      type: SideStory,
      subjectIsLater: true,
      terms: ['side story', 'sidestory', 'gaiden'],
      phrases: ['side story', 'side-story', 'sidestory', 'gaiden'],
    },
    {
      key: 'spinoff',
      label: 'Spin-off',
      type: Spinoff,
      subjectIsLater: true,
      terms: ['spin-off', 'spinoff', 'spin off'],
      phrases: ['spin-off', 'spin off', 'spinoff'],
    },
    { key: 'fandisc', label: 'Fandisc', type: Fandisc, subjectIsLater: true, terms: ['fandisc', 'fan disc', 'fd'], phrases: ['fan disc', 'fandisc'] },
    {
      key: 'adaptation',
      label: 'Adaptation',
      type: Adaptation,
      subjectIsLater: true,
      terms: ['adaptation', 'adapts', 'anime', 'film', 'movie', 'manga', 'novelization', 'novelisation'],
      phrases: ['adaptation', 'adapts'],
    },
    {
      key: 'alternative',
      label: 'Alternative version',
      type: Alternative,
      subjectIsLater: false,
      terms: ['alternative', 'alt', 'remake', 'retelling', 'reboot'],
      phrases: ['alternative', 'remake', 'alt'],
    },
  ] as (Omit<LinkChoice, 'words'> & { words?: string })[]
).map((c) => ({ ...c, words: c.words ?? linkTypeInfo(c.type).verb }));

export function linkChoiceByKey(key: string): LinkChoice | undefined {
  return linkChoices.find((c) => c.key === key);
}

export function filterLinkChoices(filter: string): LinkChoice[] {
  const f = filter.trim().toLowerCase();
  if (!f) return linkChoices;
  return linkChoices.filter((c) => c.label.toLowerCase().startsWith(f) || c.terms.some((t) => t.startsWith(f)));
}

/** The stored edge for "subject <choice.words> object". */
export function choiceToEdge(choice: LinkChoice, subject: number, object: number): FranchiseEdge {
  const edge = { sourceDeckId: object, targetDeckId: subject, relationshipType: choice.type };
  const flow = getEdgeFlow(edge);
  if (!flow.directed || (flow.to === subject) === choice.subjectIsLater) return edge;
  return { sourceDeckId: subject, targetDeckId: object, relationshipType: choice.type };
}

/** The choice and decks that reproduce a stored edge, for reopening it in the picker. */
export function choiceForEdge(edge: FranchiseEdge): { choice: LinkChoice; subject: number; object: number } | null {
  for (const choice of linkChoices) {
    for (const [subject, object] of [
      [edge.sourceDeckId, edge.targetDeckId],
      [edge.targetDeckId, edge.sourceDeckId],
    ] as const) {
      if (sameEdge(choiceToEdge(choice, subject, object), edge)) return { choice, subject, object };
    }
  }
  return null;
}

function sameEdge(a: FranchiseEdge, b: FranchiseEdge): boolean {
  return a.sourceDeckId === b.sourceDeckId && a.targetDeckId === b.targetDeckId && a.relationshipType === b.relationshipType;
}

const samePair = (a: FranchiseEdge, b: FranchiseEdge) =>
  (a.sourceDeckId === b.sourceDeckId && a.targetDeckId === b.targetDeckId) || (a.sourceDeckId === b.targetDeckId && a.targetDeckId === b.sourceDeckId);

export const activeEdges = (state: BuilderState) => state.edges.filter((e) => e.status !== 'removed');
export const activeMembers = (state: BuilderState) => state.members.filter((m) => m.status !== 'removed');

export const hasLinks = (state: BuilderState, deckId: number) => activeEdges(state).some((e) => e.sourceDeckId === deckId || e.targetDeckId === deckId);
export const hasMembership = (state: BuilderState, deckId: number) => activeMembers(state).some((m) => m.deckId === deckId);

function findStoryPath(edges: FranchiseEdge[], start: number, goal: number): number[] | null {
  const next = new Map<number, number[]>();
  for (const e of edges) {
    const flow = getEdgeFlow(e);
    if (!flow.directed) continue;
    if (!next.has(flow.from)) next.set(flow.from, []);
    next.get(flow.from)!.push(flow.to);
  }
  const prev = new Map<number, number | null>([[start, null]]);
  const queue = [start];
  while (queue.length) {
    const n = queue.shift()!;
    if (n === goal) {
      const path: number[] = [];
      for (let k: number | null = n; k != null; k = prev.get(k) ?? null) path.unshift(k);
      return path;
    }
    for (const m of next.get(n) ?? []) {
      if (prev.has(m)) continue;
      prev.set(m, n);
      queue.push(m);
    }
  }
  return null;
}

export interface LinkCheck {
  error?: string;
  /** The link between the same two decks that this one would replace; one link per pair. */
  replaces?: BuilderEdge;
}

export function checkLink(state: BuilderState, edge: FranchiseEdge, name: (deckId: number) => string, ignoreId: number | null = null): LinkCheck {
  if (edge.sourceDeckId === edge.targetDeckId) return { error: "A deck can't be linked to itself." };
  const others = activeEdges(state).filter((e) => e.id !== ignoreId);
  const existing = others.find((e) => samePair(e, edge));
  const flow = getEdgeFlow(edge);
  if (existing && existing.relationshipType === edge.relationshipType && (sameEdge(existing, edge) || !flow.directed))
    return { error: 'These two are already linked this way.' };
  if (flow.directed) {
    const path = findStoryPath(
      others.filter((e) => e !== existing),
      flow.to,
      flow.from
    );
    if (path)
      return {
        error: `That would make a loop: ${path.map(name).join(' → ')} is already set, so ${name(flow.to)} can't also come after ${name(flow.from)}.`,
      };
  }
  return existing ? { replaces: existing } : {};
}

function nextEdgeId(state: BuilderState): number {
  return state.edges.reduce((max, e) => Math.max(max, e.id), 0) + 1;
}

export function dropLink(state: BuilderState, id: number): void {
  const edge = state.edges.find((e) => e.id === id);
  if (!edge) return;
  if (edge.status === 'saved') edge.status = 'removed';
  else if (edge.status === 'new') state.edges = state.edges.filter((e) => e.id !== id);
}

export function applyLink(state: BuilderState, edge: FranchiseEdge, replaces?: BuilderEdge | null): void {
  if (replaces) dropLink(state, replaces.id);
  for (const id of [edge.sourceDeckId, edge.targetDeckId]) if (!state.board.includes(id)) state.board.push(id);
  const twin = state.edges.find((e) => e.status === 'removed' && sameEdge(e, edge));
  if (twin) {
    twin.status = 'saved';
    return;
  }
  state.edges.push({ ...pickEdge(edge), id: nextEdgeId(state), status: 'new' });
}

const pickEdge = (e: FranchiseEdge): FranchiseEdge => ({ sourceDeckId: e.sourceDeckId, targetDeckId: e.targetDeckId, relationshipType: e.relationshipType });

/** Links each deck as a sequel to the one before it, skipping pairs already linked or that would make a loop. */
export function chainSequels(state: BuilderState, ordered: number[]): { made: number; skipped: [number, number][] } {
  const sequel = linkChoiceByKey('sequel')!;
  let made = 0;
  const skipped: [number, number][] = [];
  for (let i = 0; i + 1 < ordered.length; i++) {
    const e = choiceToEdge(sequel, ordered[i + 1]!, ordered[i]!);
    const v = checkLink(state, e, String);
    if (v.error || v.replaces) {
      skipped.push([ordered[i]!, ordered[i + 1]!]);
      continue;
    }
    applyLink(state, e);
    made++;
  }
  return { made, skipped };
}

export function joinGroup(state: BuilderState, seriesId: number, deckId: number): void {
  if (!state.board.includes(deckId)) state.board.push(deckId);
  const m = state.members.find((x) => x.seriesId === seriesId && x.deckId === deckId);
  if (m) {
    if (m.status === 'removed') m.status = 'saved';
    return;
  }
  state.members.push({ seriesId, deckId, status: 'new' });
}

export function leaveGroup(state: BuilderState, seriesId: number, deckId: number): void {
  const m = state.members.find((x) => x.seriesId === seriesId && x.deckId === deckId);
  if (!m) return;
  if (m.status === 'saved') m.status = 'removed';
  else if (m.status === 'new') state.members = state.members.filter((x) => x !== m);
}

/** Decks joined by story links; settings never join lines. */
function storyLines(board: number[], edges: FranchiseEdge[]): number[][] {
  const uf = new UnionFind(board);
  for (const e of edges) uf.join(e.sourceDeckId, e.targetDeckId);
  return uf.groups(board);
}

export function lineOf(state: BuilderState, deckId: number): number[] {
  return storyLines(state.board, activeEdges(state)).find((l) => l.includes(deckId)) ?? [deckId];
}

/** The decks of `deckId`'s line that are direct members of the series. */
export function lineInSeries(state: BuilderState, seriesId: number, deckId: number): number[] {
  const line = lineOf(state, deckId);
  return activeMembers(state)
    .filter((m) => m.seriesId === seriesId && line.includes(m.deckId))
    .map((m) => m.deckId);
}

/** Series ids the story line belongs to, settings excluded. */
function seriesOfLine(state: BuilderState, series: BuilderSeries[], line: number[]): number[] {
  const kinds = new Map(series.map((s) => [s.seriesId, s.kind]));
  return [
    ...new Set(
      activeMembers(state)
        .filter((m) => kinds.get(m.seriesId) === SeriesKind.Series && line.includes(m.deckId))
        .map((m) => m.seriesId)
    ),
  ];
}

export function otherSeriesOfLine(state: BuilderState, series: BuilderSeries[], seriesId: number, deckId: number): number[] {
  return seriesOfLine(state, series, lineOf(state, deckId)).filter((x) => x !== seriesId);
}

/** A line sits in one series: joining another takes it out of the rest. Settings are untouched. */
function moveLineToSeries(state: BuilderState, series: BuilderSeries[], line: number[], seriesId: number, deckId: number): void {
  for (const from of seriesOfLine(state, series, line)) {
    if (from === seriesId) continue;
    for (const id of line) leaveGroup(state, from, id);
  }
  joinGroup(state, seriesId, deckId);
}

/** A series takes each deck's line through its first deck by `order`, out of any other series; a setting takes each deck alone. Returns the decks that joined. */
export function putLinesInSeries(
  state: BuilderState,
  series: BuilderSeries[],
  deckIds: number[],
  seriesId: number,
  order: (ids: number[]) => number[]
): number[] {
  if (series.find((s) => s.seriesId === seriesId)?.kind === SeriesKind.Setting) {
    const fresh = [...new Set(deckIds)].filter((id) => !activeMembers(state).some((m) => m.seriesId === seriesId && m.deckId === id));
    for (const id of fresh) joinGroup(state, seriesId, id);
    return fresh;
  }
  const lines = new Map<number, number[]>();
  for (const id of deckIds) {
    const line = lineOf(state, id);
    lines.set(Math.min(...line), line);
  }
  const entries: number[] = [];
  for (const line of lines.values()) {
    if (lineInSeries(state, seriesId, line[0]!).length) continue;
    const entry = order(line.filter((id) => deckIds.includes(id)))[0]!;
    moveLineToSeries(state, series, line, seriesId, entry);
    entries.push(entry);
  }
  return entries;
}

export function revertLink(state: BuilderState, id: number): void {
  const edge = state.edges.find((e) => e.id === id);
  if (!edge) return;
  if (edge.status === 'new') state.edges = state.edges.filter((e) => e.id !== id);
  else edge.status = 'saved';
}

export function revertMembership(state: BuilderState, seriesId: number, deckId: number): void {
  const m = state.members.find((x) => x.seriesId === seriesId && x.deckId === deckId);
  if (!m) return;
  if (m.status === 'new') state.members = state.members.filter((x) => x !== m);
  else m.status = 'saved';
}

export function discardPending(state: BuilderState): void {
  state.edges = state.edges.filter((e) => e.status !== 'new').map((e) => ({ ...e, status: 'saved' }));
  state.members = state.members.filter((m) => m.status !== 'new').map((m) => ({ ...m, status: 'saved' }));
}

export type PendingChange = { kind: 'link'; edge: BuilderEdge } | { kind: 'member'; member: BuilderMembership };

export function pendingChanges(state: BuilderState): PendingChange[] {
  return [
    ...state.edges.filter((e) => e.status !== 'saved').map((edge): PendingChange => ({ kind: 'link', edge })),
    ...state.members.filter((m) => m.status !== 'saved').map((member): PendingChange => ({ kind: 'member', member })),
  ];
}

export function buildSaveRequest(anchorDeckId: number, state: BuilderState): FranchiseBuilderSaveRequest {
  const member = (m: BuilderMembership): FranchiseBuilderMember => ({ seriesId: m.seriesId, deckId: m.deckId });
  return {
    anchorDeckId,
    addEdges: state.edges.filter((e) => e.status === 'new').map(pickEdge),
    removeEdges: state.edges.filter((e) => e.status === 'removed').map(pickEdge),
    addMembers: state.members.filter((m) => m.status === 'new').map(member),
    removeMembers: state.members.filter((m) => m.status === 'removed').map(member),
  };
}

export function builderStateFromFranchise(franchise: Franchise): BuilderState {
  return {
    board: franchise.nodes.map((n) => n.deckId),
    edges: franchise.edges
      .filter((e) => !isLegacyGroupRelationship(e.relationshipType))
      .map((e, i) => ({ ...pickEdge(e), id: i + 1, status: 'saved' as const })),
    members: [
      ...(franchise.series ?? []).flatMap((s) => s.memberDeckIds.map((deckId) => ({ seriesId: s.seriesId, deckId, status: 'saved' as const }))),
      ...(franchise.settings ?? []).flatMap((s) => s.memberDeckIds.map((deckId) => ({ seriesId: s.seriesId, deckId, status: 'saved' as const }))),
    ],
  };
}

export function builderSeriesFromFranchise(franchise: Franchise): BuilderSeries[] {
  return [
    ...(franchise.series ?? []).map((s) => ({ seriesId: s.seriesId, name: s.name, kind: SeriesKind.Series })),
    ...(franchise.settings ?? []).map((s) => ({ seriesId: s.seriesId, name: s.name, kind: SeriesKind.Setting, outsideCount: s.outsideCount })),
  ];
}

/** Franchises as Jiten will see them after saving: story lines merged through each series. */
function franchiseGroups(state: BuilderState, series: BuilderSeries[]): number[][] {
  const uf = new UnionFind(state.board);
  for (const e of activeEdges(state)) uf.join(e.sourceDeckId, e.targetDeckId);
  const kinds = new Map(series.map((s) => [s.seriesId, s.kind]));
  const firstBySeries = new Map<number, number>();
  for (const m of activeMembers(state)) {
    if (kinds.get(m.seriesId) !== SeriesKind.Series || !state.board.includes(m.deckId)) continue;
    const first = firstBySeries.get(m.seriesId);
    if (first == null) firstBySeries.set(m.seriesId, m.deckId);
    else uf.join(first, m.deckId);
  }
  return uf.groups(state.board);
}

/** The smaller part the franchise splits off into once the link is gone; empty when it stays whole. */
export function cutOffBy(state: BuilderState, series: BuilderSeries[], edgeId: number): number[] {
  const e = state.edges.find((x) => x.id === edgeId);
  if (!e || e.status === 'removed') return [];
  const groups = franchiseGroups({ ...state, edges: state.edges.filter((x) => x.id !== edgeId) }, series);
  const a = groups.find((g) => g.includes(e.sourceDeckId));
  const b = groups.find((g) => g.includes(e.targetDeckId));
  if (!a || !b || a === b) return [];
  return a.length <= b.length ? a : b;
}

export type BuilderCheck =
  | { kind: 'outside'; lines: number[][] }
  | { kind: 'unlinked'; deckIds: number[] }
  | { kind: 'branch'; deckId: number; sequels: number[] }
  | { kind: 'order'; edge: BuilderEdge }
  | { kind: 'ok'; deckCount: number };

export function builderChecks(state: BuilderState, series: BuilderSeries[], year: (deckId: number) => number | null): BuilderCheck[] {
  const out: BuilderCheck[] = [];
  const edges = activeEdges(state);
  const seriesIds = new Set(series.filter((s) => s.kind === SeriesKind.Series).map((s) => s.seriesId));
  const inSeries = new Set(
    activeMembers(state)
      .filter((m) => seriesIds.has(m.seriesId))
      .map((m) => m.deckId)
  );
  const lines = storyLines(state.board, edges);
  const outside = lines.filter((l) => !l.some((id) => inSeries.has(id)));
  const groupCount = franchiseGroups(state, series).length;
  const relevant = inSeries.size > 0 || outside.length > 1;

  const outsideLines = outside.filter((l) => l.length > 1);
  if (relevant && outsideLines.length) out.push({ kind: 'outside', lines: outsideLines });
  const loose = outside.filter((l) => l.length === 1).map((l) => l[0]!);
  if (relevant && loose.length && state.board.length > 1) out.push({ kind: 'unlinked', deckIds: loose });

  for (const id of state.board) {
    const sequels = edges.filter((e) => e.relationshipType === Sequel && getEdgeFlow(e).from === id).map((e) => getEdgeFlow(e).to);
    if (sequels.length > 1) out.push({ kind: 'branch', deckId: id, sequels });
  }
  for (const e of edges) {
    if (e.relationshipType !== Sequel) continue;
    const flow = getEdgeFlow(e);
    const a = year(flow.from);
    const b = year(flow.to);
    if (a != null && b != null && b < a) out.push({ kind: 'order', edge: e });
  }
  if (groupCount === 1 && state.board.length > 0 && !out.some((c) => c.kind === 'outside' || c.kind === 'unlinked'))
    out.unshift({ kind: 'ok', deckCount: state.board.length });
  return out;
}

const CARD_W = 176;
const CARD_H = 80;
const GAP_X = 96;
const GAP_Y = 24;
const BOARD_PAD = 20;
const BAND_TOP = 34;
const BAND_GAP = 18;
const SHELF_GAP = 14;
const RAIL_W = 48;
const SERIES_HEAD = 34;
const SECTION_GAP = 14;

export const BOARD_METRICS = { cardW: CARD_W, cardH: CARD_H, pad: BOARD_PAD, railW: RAIL_W } as const;

export type BandKind = 'line' | 'run' | 'empty' | 'outside' | 'unlinked';

export interface BoardBand {
  kind: BandKind;
  x: number;
  y: number;
  w: number;
  h: number;
  ids: number[];
  /** Decks that carry the series membership for this band. */
  via: number[];
  seriesId: number | null;
}

export interface RailNode {
  y: number;
  ids: number[];
  via: number[];
  kind: 'line' | 'run' | 'empty';
}

export interface RailSection {
  seriesId: number;
  x: number;
  headY: number;
  railX: number;
  dropTop: number;
  dropBottom: number;
  nodes: RailNode[];
  deckCount: number;
}

export interface BoardLayout {
  cards: Map<number, { x: number; y: number }>;
  bands: BoardBand[];
  sections: RailSection[];
  /** The series each card is shown under. */
  seriesOf: Map<number, number>;
  width: number;
  height: number;
}

export interface BoardLayoutInput {
  board: number[];
  edges: FranchiseEdge[];
  /** Active memberships; settings are ignored. */
  members: { seriesId: number; deckId: number }[];
  series: BuilderSeries[];
  year: (deckId: number) => number | null;
  title: (deckId: number) => string;
  /** Breaks slot ties inside a story line, as the public Series view does. */
  compareRelease: (a: number, b: number) => number;
  width: number;
}

const yearKey = (y: number | null) => y ?? 99999;

export function byRelease(ids: number[], input: Pick<BoardLayoutInput, 'year' | 'title'>): number[] {
  return [...ids].sort((a, b) => yearKey(input.year(a)) - yearKey(input.year(b)) || input.title(a).localeCompare(input.title(b)) || a - b);
}

/** Series (settings excluded) by their first release, then by name. */
export function orderSeries(series: BuilderSeries[], firstYear: (seriesId: number) => number | null): BuilderSeries[] {
  return series
    .filter((s) => s.kind === SeriesKind.Series)
    .sort((a, b) => yearKey(firstYear(a.seriesId)) - yearKey(firstYear(b.seriesId)) || a.name.localeCompare(b.name));
}

export function layoutBoard(input: BoardLayoutInput): BoardLayout {
  const W = Math.max(input.width, 320);
  const boardSet = new Set(input.board);
  const edges = input.edges;
  const seriesIds = new Set(input.series.filter((s) => s.kind === SeriesKind.Series).map((s) => s.seriesId));
  const members = input.members.filter((m) => boardSet.has(m.deckId) && seriesIds.has(m.seriesId));
  const seriesOfDeck = new Map<number, number[]>();
  for (const m of members) {
    if (!seriesOfDeck.has(m.deckId)) seriesOfDeck.set(m.deckId, []);
    seriesOfDeck.get(m.deckId)!.push(m.seriesId);
  }

  const units = storyLines(input.board, edges).map((ids) => ({
    ids,
    via: byRelease(
      ids.filter((id) => seriesOfDeck.has(id)),
      input
    ),
  }));
  const firstYearOfSeries = (seriesId: number) => {
    const ys = members
      .filter((m) => m.seriesId === seriesId)
      .map((m) => input.year(m.deckId))
      .filter((y): y is number => y != null);
    return ys.length ? Math.min(...ys) : null;
  };
  const order = orderSeries(input.series, firstYearOfSeries);
  const orderIndex = new Map(order.map((s, i) => [s.seriesId, i]));

  // A line spanning several series is listed under the first of them.
  const unitsBySeries = new Map<number, typeof units>();
  for (const u of units) {
    if (!u.via.length) continue;
    const candidates = [...new Set(u.via.flatMap((id) => seriesOfDeck.get(id)!))];
    candidates.sort((a, b) => (orderIndex.get(a) ?? 0) - (orderIndex.get(b) ?? 0));
    const sid = candidates[0]!;
    if (!unitsBySeries.has(sid)) unitsBySeries.set(sid, []);
    unitsBySeries.get(sid)!.push(u);
  }

  const cards = new Map<number, { x: number; y: number }>();
  const bands: BoardBand[] = [];
  const sections: RailSection[] = [];
  const seriesOf = new Map<number, number>();
  let y = BOARD_PAD;
  let maxRight = 0;

  const placeLine = (comp: number[], x0: number, kind: BandKind, via: number[], seriesId: number | null = null) => {
    const lay = layoutStoryLine(comp, edges, input.compareRelease);
    for (const n of comp) {
      const p = lay.pos.get(n)!;
      cards.set(n, { x: x0 + 14 + p.layer * (CARD_W + GAP_X), y: y + BAND_TOP + p.slot * (CARD_H + GAP_Y) });
    }
    const h = BAND_TOP + lay.slots * (CARD_H + GAP_Y) - GAP_Y + 16;
    maxRight = Math.max(maxRight, x0 + lay.layers * (CARD_W + GAP_X) - GAP_X + 28);
    const top = y;
    bands.push({ kind, x: x0, y: top, w: 0, h, ids: comp, via, seriesId });
    y += h + BAND_GAP;
    return top;
  };
  const placeGrid = (list: number[], x0: number, kind: BandKind, via: number[], seriesId: number | null = null) => {
    const avail = Math.max(W, maxRight + BOARD_PAD) - BOARD_PAD - x0;
    const cols = Math.max(1, Math.floor((avail - 28 + SHELF_GAP) / (CARD_W + SHELF_GAP)));
    list.forEach((n, i) => cards.set(n, { x: x0 + 14 + (i % cols) * (CARD_W + SHELF_GAP), y: y + BAND_TOP + Math.floor(i / cols) * (CARD_H + SHELF_GAP + 6) }));
    const rowsN = Math.max(1, Math.ceil(list.length / cols));
    const h = list.length ? BAND_TOP + rowsN * (CARD_H + SHELF_GAP + 6) - SHELF_GAP - 6 + 16 : BAND_TOP + 30;
    maxRight = Math.max(maxRight, x0 + Math.min(Math.max(list.length, 1), cols) * (CARD_W + SHELF_GAP) - SHELF_GAP + 28);
    const top = y;
    bands.push({ kind, x: x0, y: top, w: 0, h, ids: list, via, seriesId });
    y += h + BAND_GAP;
    return top;
  };

  for (const series of order) {
    const x0 = BOARD_PAD;
    const headY = y;
    y += SERIES_HEAD;
    const sx = x0 + RAIL_W;
    const nodes: RailNode[] = [];
    const list = (unitsBySeries.get(series.seriesId) ?? []).sort(
      (a, b) => yearKey(input.year(a.via[0]!)) - yearKey(input.year(b.via[0]!)) || byRelease(a.ids, input)[0]! - byRelease(b.ids, input)[0]!
    );
    if (!list.length) {
      const top = placeGrid([], sx, 'empty', [], series.seriesId);
      nodes.push({ y: top + 32, ids: [], via: [], kind: 'empty' });
    }
    for (let i = 0; i < list.length; ) {
      const u = list[i]!;
      if (u.ids.length > 1) {
        placeLine(u.ids, sx, 'line', u.via, series.seriesId);
        nodes.push({ y: cards.get(u.via[0]!)!.y + CARD_H / 2, ids: u.ids, via: u.via, kind: 'line' });
        i++;
        continue;
      }
      const run: number[] = [];
      while (i < list.length && list[i]!.ids.length === 1) run.push(list[i++]!.ids[0]!);
      const top = placeGrid(run, sx, 'run', run, series.seriesId);
      nodes.push({ y: top + BAND_TOP + CARD_H / 2, ids: run, via: run, kind: 'run' });
    }
    for (const n of nodes) for (const id of n.ids) seriesOf.set(id, series.seriesId);
    sections.push({
      seriesId: series.seriesId,
      x: x0,
      headY,
      railX: x0 + RAIL_W / 2 - 4,
      dropTop: headY,
      dropBottom: y - BAND_GAP,
      nodes,
      deckCount: list.reduce((s, u) => s + u.ids.length, 0),
    });
    y += SECTION_GAP;
  }

  const rest = units.filter((u) => !u.via.length);
  const lines = rest
    .filter((u) => u.ids.length > 1)
    .sort((a, b) => yearKey(input.year(byRelease(a.ids, input)[0]!)) - yearKey(input.year(byRelease(b.ids, input)[0]!)));
  for (const u of lines) placeLine(u.ids, BOARD_PAD, 'outside', []);
  const loose = byRelease(
    rest.filter((u) => u.ids.length === 1).map((u) => u.ids[0]!),
    input
  );
  if (loose.length) placeGrid(loose, BOARD_PAD, 'unlinked', []);

  const width = Math.max(W, maxRight + BOARD_PAD);
  for (const b of bands) b.w = width - BOARD_PAD - b.x;
  return { cards, bands, sections, seriesOf, width, height: Math.max(320, y - BAND_GAP + BOARD_PAD) };
}

export type CardRemoveAction = { kind: 'leave'; seriesId: number } | { kind: 'remove' };

/** A card leaves the series it is shown under, else the board; null while links or memberships still hold it there. */
export function cardRemoveAction(state: BuilderState, layout: Pick<BoardLayout, 'seriesOf'>, deckId: number): CardRemoveAction | null {
  const seriesId = layout.seriesOf.get(deckId);
  if (seriesId != null) return { kind: 'leave', seriesId };
  return hasLinks(state, deckId) || hasMembership(state, deckId) ? null : { kind: 'remove' };
}

export type BuilderDropTarget = { kind: 'deck'; id: number } | { kind: 'series'; id: number } | { kind: 'setting'; id: number } | { kind: 'board' };

/** Reads the nearest `data-drop` ("deck:12", "series:3", "setting:4" or "board") at or above an element. */
export function dropTargetOf(el: Element | null): BuilderDropTarget | null {
  const raw = el?.closest('[data-drop]')?.getAttribute('data-drop');
  if (!raw) return null;
  if (raw === 'board') return { kind: 'board' };
  const [kind, id] = raw.split(':');
  const n = Number(id);
  if (!Number.isFinite(n)) return null;
  if (kind === 'deck' || kind === 'series' || kind === 'setting') return { kind, id: n };
  return null;
}

export function sameDropTarget(a: BuilderDropTarget | null, b: BuilderDropTarget | null): boolean {
  if (!a || !b) return a === b;
  return a.kind === b.kind && (a.kind === 'board' || a.id === (b as { id: number }).id);
}
