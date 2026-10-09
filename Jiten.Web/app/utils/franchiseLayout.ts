import type { RouteLocationRaw } from 'vue-router';
import { DeckRelationshipType } from '~/types/enums';
import type { Franchise, FranchiseEdge, FranchiseNode } from '~/types/types';
import type { FranchiseViewKind } from '~/types/series';
import { difficultyBand, difficultyNames } from '~/utils/difficultyColours';
import { getEdgeFlow, isLegacyGroupRelationship, relationshipLinkTypes } from '~/utils/relationshipRoles';

export type FranchiseLinkMode = 'story' | 'all' | 'none';
export type FranchiseCardSize = 'covers' | 'compact';
export type FranchiseSeriesDensity = 'detailed' | 'compact' | 'main';

export interface FranchisePopoverMembership {
  key: string;
  kind: 'series' | 'setting';
  seriesId: number;
  label: string;
  name: string;
  to?: RouteLocationRaw;
  /** Shown as plain text: the view has nothing to do on a click. */
  inert?: boolean;
}

const DERIVED_TYPES = new Set([DeckRelationshipType.SideStory, DeckRelationshipType.Spinoff, DeckRelationshipType.Fandisc, DeckRelationshipType.Adaptation]);

/** "Side story of" and the like; null for sequels and alternatives, which a card never names. */
export function franchiseDerivedLabel(type: DeckRelationshipType): string | null {
  const verb = DERIVED_TYPES.has(type) ? relationshipLinkTypes[type]?.verb : undefined;
  if (!verb) return null;
  const phrase = verb.replace(/^is an? /, '');
  return phrase.charAt(0).toUpperCase() + phrase.slice(1);
}

/** Sequels and alternative versions: the links the Story mode keeps drawing. */
export function isStoryRelation(type: DeckRelationshipType): boolean {
  return type === DeckRelationshipType.Sequel || type === DeckRelationshipType.Alternative;
}

/** Without a side link, the Story and All link modes draw the same thing. */
export function franchiseHasSideLinks(edges: FranchiseEdge[]): boolean {
  return edges.some((e) => !isStoryRelation(e.relationshipType));
}

export function franchiseEntryCountLabel(shown: number, total: number): string {
  const noun = total === 1 ? 'entry' : 'entries';
  return shown === total ? `${total.toLocaleString()} ${noun}` : `${shown.toLocaleString()} of ${total.toLocaleString()} ${noun}`;
}

/** Edges between two present decks, without the legacy series/setting types. */
export function franchiseUsableEdges(nodes: FranchiseNode[], edges: FranchiseEdge[]): FranchiseEdge[] {
  const ids = new Set(nodes.map((n) => n.deckId));
  return edges.filter(
    (e) => !isLegacyGroupRelationship(e.relationshipType) && e.sourceDeckId !== e.targetDeckId && ids.has(e.sourceDeckId) && ids.has(e.targetDeckId)
  );
}

/** Unset release dates arrive as the DateOnly default (year 1). UTC, since a bare date parses as UTC midnight. */
export function releaseYearOf(releaseDate: string | null | undefined): number | null {
  if (!releaseDate) return null;
  const t = Date.parse(releaseDate);
  if (Number.isNaN(t)) return null;
  const year = new Date(t).getUTCFullYear();
  return year <= 1 ? null : year;
}

type ReleaseOrdered = Pick<FranchiseNode, 'deckId' | 'releaseDate'>;

function releaseTime(node: ReleaseOrdered): number {
  return releaseYearOf(node.releaseDate) == null ? Number.POSITIVE_INFINITY : Date.parse(node.releaseDate);
}

export function compareFranchiseRelease(a: ReleaseOrdered, b: ReleaseOrdered): number {
  const ta = releaseTime(a);
  const tb = releaseTime(b);
  if (ta !== tb) return ta < tb ? -1 : 1;
  return a.deckId - b.deckId;
}

export function franchiseFirstNode<T extends ReleaseOrdered>(nodes: T[]): T | null {
  let first: T | null = null;
  for (const n of nodes) if (!first || compareFranchiseRelease(n, first) < 0) first = n;
  return first;
}

/** The stored name (localised when it is an entry's title, lowest deck id first, as the admin list does), else the earliest entry's title. */
export function franchiseDisplayName(franchise: Pick<Franchise, 'name' | 'nodes'>, localise: (node: FranchiseNode) => string): string {
  if (franchise.name) {
    let source: FranchiseNode | null = null;
    for (const n of franchise.nodes) if (n.originalTitle === franchise.name && (!source || n.deckId < source.deckId)) source = n;
    return source ? localise(source) : franchise.name;
  }
  const first = franchiseFirstNode(franchise.nodes);
  return first ? localise(first) : '';
}

export interface DerivedChip {
  type: DeckRelationshipType;
  originId: number;
  extra: number;
}

/** What each deck is a side story, spin-off, fandisc or adaptation of; the earliest-released original is named, the rest counted. */
export function franchiseDerivedChips(edges: FranchiseEdge[], nodeById: Map<number, FranchiseNode>): Map<number, DerivedChip> {
  const origins = new Map<number, { type: DeckRelationshipType; originId: number }[]>();
  for (const e of edges) {
    if (!DERIVED_TYPES.has(e.relationshipType)) continue;
    const flow = getEdgeFlow(e);
    if (!nodeById.has(flow.from)) continue;
    const list = origins.get(flow.to) ?? [];
    list.push({ type: e.relationshipType, originId: flow.from });
    origins.set(flow.to, list);
  }
  const chips = new Map<number, DerivedChip>();
  for (const [deckId, list] of origins) {
    list.sort((a, b) => compareFranchiseRelease(nodeById.get(a.originId)!, nodeById.get(b.originId)!));
    chips.set(deckId, { ...list[0]!, extra: list.length - 1 });
  }
  return chips;
}

/** Popover rows for a deck: its series (direct, or through its line's entry when `links` is given) and its settings. */
export function franchisePopoverMemberships(
  franchise: Pick<Franchise, 'series' | 'settings'>,
  deckId: number,
  title?: (deckId: number) => string | null,
  links?: DeckSeriesLink[]
): FranchisePopoverMembership[] {
  const series = franchise.series ?? [];
  const names = new Map(series.map((s) => [s.seriesId, s.name]));
  const own = links ?? series.filter((s) => s.memberDeckIds.includes(deckId)).map((s) => ({ seriesId: s.seriesId, viaDeckId: null }));
  const out: FranchisePopoverMembership[] = [];
  for (const link of own) {
    const name = names.get(link.seriesId);
    if (!name) continue;
    const via = link.viaDeckId != null ? (title?.(link.viaDeckId) ?? null) : null;
    out.push({ key: `s-${link.seriesId}`, kind: 'series', seriesId: link.seriesId, label: 'Series', name: via ? `${name} (through ${via})` : name });
  }
  for (const s of franchise.settings ?? []) {
    if (s.memberDeckIds.includes(deckId)) out.push({ key: `t-${s.seriesId}`, kind: 'setting', seriesId: s.seriesId, label: 'Setting', name: s.name });
  }
  return out;
}

export interface StoryLineLayout {
  /** Layer is the left-to-right step, slot the vertical position inside the layer. */
  pos: Map<number, { layer: number; slot: number }>;
  layers: number;
  slots: number;
  edges: FranchiseEdge[];
}

/** Layered left-to-right layout of one story line: an original sits before what follows from it, alternatives level with their counterpart. `compare` breaks slot ties. */
export function layoutStoryLine(ids: number[], edges: FranchiseEdge[], compare: (a: number, b: number) => number): StoryLineLayout {
  const inside = new Set(ids);
  const lineEdges = edges.filter((e) => inside.has(e.sourceDeckId) && inside.has(e.targetDeckId));
  const flows = lineEdges.map(getEdgeFlow);
  const directed = flows.filter((f) => f.directed);
  const undirected = flows.filter((f) => !f.directed);
  const maxLayer = Math.max(0, ids.length - 1);
  const layer = new Map<number, number>(ids.map((id) => [id, 0]));

  const propagate = () => {
    for (let i = 0; i < ids.length; i++) {
      let changed = false;
      for (const { from, to } of directed) {
        const want = Math.min(maxLayer, layer.get(from)! + 1);
        if (layer.get(to)! < want) {
          layer.set(to, want);
          changed = true;
        }
      }
      if (!changed) break;
    }
  };
  propagate();

  const hasIn = new Set(directed.map((d) => d.to));
  const hasOut = new Set(directed.map((d) => d.from));
  const undirectedNeighbours = (id: number) => undirected.flatMap((u) => (u.from === id ? [u.to] : u.to === id ? [u.from] : []));

  for (const id of ids) {
    if (hasIn.has(id) || !hasOut.has(id)) continue;
    const next = directed.filter((d) => d.from === id).map((d) => layer.get(d.to)!);
    layer.set(id, Math.max(0, Math.min(...next) - 1));
    const linked = undirectedNeighbours(id).filter((m) => hasIn.has(m) || hasOut.has(m));
    if (linked.length) layer.set(id, Math.max(layer.get(id)!, ...linked.map((m) => layer.get(m)!)));
  }
  for (const id of ids) {
    if (hasIn.has(id) || hasOut.has(id)) continue;
    const linked = undirectedNeighbours(id).filter((m) => hasIn.has(m) || hasOut.has(m));
    if (linked.length) layer.set(id, Math.max(...linked.map((m) => layer.get(m)!)));
  }
  propagate();

  const neighbours = (id: number) => lineEdges.flatMap((e) => (e.sourceDeckId === id ? [e.targetDeckId] : e.targetDeckId === id ? [e.sourceDeckId] : []));
  const slot = new Map<number, number>();
  let slots = 0;
  const layers = Math.max(...ids.map((id) => layer.get(id)!)) + 1;
  for (let l = 0; l < layers; l++) {
    const taken = new Set<number>();
    const items = ids
      .filter((id) => layer.get(id) === l)
      .map((id) => {
        const placed = neighbours(id).filter((m) => slot.has(m));
        return { id, want: placed.length ? placed.reduce((s, m) => s + slot.get(m)!, 0) / placed.length : null };
      })
      .sort((a, b) => (a.want ?? Infinity) - (b.want ?? Infinity) || compare(a.id, b.id));
    for (const { id, want } of items) {
      const base = want == null ? 0 : Math.round(want);
      let s = base;
      for (let k = 0; ; k++) {
        if (!taken.has(base + k)) {
          s = base + k;
          break;
        }
        if (base - k >= 0 && !taken.has(base - k)) {
          s = base - k;
          break;
        }
      }
      taken.add(s);
      slot.set(id, s);
      slots = Math.max(slots, s + 1);
    }
  }

  const pos = new Map(ids.map((id) => [id, { layer: layer.get(id)!, slot: slot.get(id)! }]));
  return { pos, layers, slots, edges: lineEdges };
}

export function storyLineOrder(layout: StoryLineLayout): number[] {
  return [...layout.pos.entries()].sort((a, b) => a[1].layer - b[1].layer || a[1].slot - b[1].slot).map(([id]) => id);
}

export interface SeriesRow {
  key: string;
  kind: 'line' | 'standalone';
  /** Release order. */
  deckIds: number[];
  /** The earliest series member of a line (the earliest deck outside a series); the first deck of a standalone row. */
  entryId: number;
}

export interface SeriesGroup {
  seriesId: number;
  name: string;
  rows: SeriesRow[];
  deckIds: number[];
}

export interface DeckSeriesLink {
  seriesId: number;
  /** Set when the deck reaches the series through the entry of its story line rather than direct membership. */
  viaDeckId: number | null;
}

export interface FranchiseSeriesLayout {
  groups: SeriesGroup[];
  /** Story lines with no series member, in release order. */
  unassigned: SeriesRow[];
  /** The groups' rows in order, then the unassigned ones. */
  rows: SeriesRow[];
  rowOf: Map<number, SeriesRow>;
  deckSeries: Map<number, DeckSeriesLink[]>;
  /** Story lines holding at least one series member. */
  entryCount: number;
}

/** Story lines (any link but settings) placed under the series of their earliest member, the lowest series id when it has several. */
export function buildFranchiseSeriesLayout(franchise: Pick<Franchise, 'nodes' | 'lines' | 'series'>): FranchiseSeriesLayout {
  const nodes = [...franchise.nodes].sort(compareFranchiseRelease);
  const nodeById = new Map(nodes.map((n) => [n.deckId, n]));
  const rank = new Map(nodes.map((n, i) => [n.deckId, i]));
  const byRank = (a: number, b: number) => rank.get(a)! - rank.get(b)!;
  const covered = new Set<number>();
  const lines: number[][] = [];
  for (const line of franchise.lines ?? []) {
    const ids = line.deckIds.filter((id) => nodeById.has(id) && !covered.has(id));
    for (const id of ids) covered.add(id);
    if (ids.length) lines.push(ids.sort(byRank));
  }
  for (const n of nodes) if (!covered.has(n.deckId)) lines.push([n.deckId]);

  const direct = new Map<number, number[]>();
  for (const s of franchise.series ?? []) {
    for (const deckId of s.memberDeckIds) {
      if (!nodeById.has(deckId)) continue;
      const list = direct.get(deckId) ?? [];
      if (!list.includes(s.seriesId)) list.push(s.seriesId);
      direct.set(deckId, list);
    }
  }

  const linesBySeries = new Map<number, number[][]>();
  const unassignedLines: number[][] = [];
  const deckSeries = new Map<number, DeckSeriesLink[]>();
  let entryCount = 0;
  for (const line of lines) {
    const entry = line.find((id) => direct.has(id));
    if (entry == null) {
      unassignedLines.push(line);
      continue;
    }
    entryCount++;
    const seriesId = Math.min(...direct.get(entry)!);
    const list = linesBySeries.get(seriesId) ?? [];
    list.push(line);
    linesBySeries.set(seriesId, list);
    for (const id of line) {
      const own = direct.get(id);
      deckSeries.set(id, own ? own.map((s) => ({ seriesId: s, viaDeckId: null })) : [{ seriesId, viaDeckId: entry }]);
    }
  }

  let rowCounter = 0;
  const rowOf = new Map<number, SeriesRow>();
  const entryOf = (line: number[]) => line.find((id) => direct.has(id)) ?? line[0]!;
  const byEntry = (a: number[], b: number[]) => byRank(entryOf(a), entryOf(b));

  const toRows = (ordered: number[][]): SeriesRow[] => {
    const rows: SeriesRow[] = [];
    for (const line of ordered) {
      const last = rows[rows.length - 1];
      if (line.length === 1 && last?.kind === 'standalone') {
        last.deckIds.push(line[0]!);
        rowOf.set(line[0]!, last);
        continue;
      }
      const row: SeriesRow = { key: `r${rowCounter++}`, kind: line.length === 1 ? 'standalone' : 'line', deckIds: [...line], entryId: entryOf(line) };
      for (const id of line) rowOf.set(id, row);
      rows.push(row);
    }
    return rows;
  };

  const groups = (franchise.series ?? [])
    .filter((s) => linesBySeries.has(s.seriesId))
    .map((s) => ({ series: s, lines: linesBySeries.get(s.seriesId)!.sort(byEntry) }))
    .sort((a, b) => byEntry(a.lines[0]!, b.lines[0]!))
    .map(({ series, lines: own }): SeriesGroup => {
      const rows = toRows(own);
      return { seriesId: series.seriesId, name: series.name, rows, deckIds: rows.flatMap((r) => r.deckIds) };
    });

  const unassigned = toRows(unassignedLines.sort((a, b) => byRank(a[0]!, b[0]!)));

  return { groups, unassigned, rows: [...groups.flatMap((g) => g.rows), ...unassigned], rowOf, deckSeries, entryCount };
}

export interface ConnectorBox {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

export interface Connector {
  path: string;
  mid: { x: number; y: number };
}

type Point = { x: number; y: number };

function cubicConnector(s: Point, c1: Point, c2: Point, t: Point): Connector {
  return {
    path: `M ${s.x} ${s.y} C ${c1.x} ${c1.y}, ${c2.x} ${c2.y}, ${t.x} ${t.y}`,
    mid: { x: (s.x + 3 * c1.x + 3 * c2.x + t.x) / 8, y: (s.y + 3 * c1.y + 3 * c2.y + t.y) / 8 },
  };
}

/** S-curve between the facing top and bottom edges of two boxes; `minBend` keeps a short hop visibly curved. */
export function verticalConnector(a: ConnectorBox, b: ConnectorBox, minBend = 0): Connector {
  const down = a.top + a.bottom <= b.top + b.bottom;
  const s = { x: (a.left + a.right) / 2, y: down ? a.bottom : a.top };
  const t = { x: (b.left + b.right) / 2, y: down ? b.top : b.bottom };
  const dy = Math.max(Math.abs(t.y - s.y) / 2, minBend) * (down ? 1 : -1);
  return cubicConnector(s, { x: s.x, y: s.y + dy }, { x: t.x, y: t.y - dy }, t);
}

/** S-curve between the facing left and right edges of two boxes. */
export function horizontalConnector(a: ConnectorBox, b: ConnectorBox, minBend = 0): Connector {
  const right = a.left < b.left;
  const s = { x: right ? a.right : a.left, y: (a.top + a.bottom) / 2 };
  const t = { x: right ? b.left : b.right, y: (b.top + b.bottom) / 2 };
  const dx = Math.max(Math.abs(t.x - s.x) / 2, minBend) * (right ? 1 : -1);
  return cubicConnector(s, { x: s.x + dx, y: s.y }, { x: t.x - dx, y: t.y }, t);
}

/** "?view=" wins; Series needs at least one series; otherwise the API's preference. */
export function resolveFranchiseView(queryView: unknown, franchise: Pick<Franchise, 'series' | 'preferredView'> | null | undefined): FranchiseViewKind {
  if (queryView === 'timeline' || queryView === 'series' || queryView === 'web') return queryView;
  return franchise?.preferredView === 'series' && (franchise.series?.length ?? 0) > 0 ? 'series' : 'timeline';
}

export function franchiseYearRange(nodes: FranchiseNode[]): string {
  const years = nodes.map((n) => releaseYearOf(n.releaseDate)).filter((y): y is number => y != null);
  if (!years.length) return '';
  const lo = Math.min(...years);
  const hi = Math.max(...years);
  return lo === hi ? String(lo) : `${lo}–${hi}`;
}

export function franchiseDifficultyRange(nodes: FranchiseNode[]): string {
  const bands = nodes.filter((n) => n.difficulty >= 0).map((n) => difficultyBand(n.difficultyRaw ?? n.difficulty));
  if (!bands.length) return '';
  const lo = Math.min(...bands);
  const hi = Math.max(...bands);
  return lo === hi ? difficultyNames[lo]! : `${difficultyNames[lo]} to ${difficultyNames[hi]}`;
}
