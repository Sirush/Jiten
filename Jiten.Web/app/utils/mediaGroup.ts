import { type Franchise, type MediaGroupRef, type MediaGroupTitles, type MediaType, MediaGroupKind } from '~/types';
import { compareFranchiseRelease } from '~/utils/franchiseLayout';
import { getMediaTypePluralText } from '~/utils/mediaTypeMapper';

type Localise = (title: MediaGroupTitles) => string;

const SCOPE_PREFIX: Partial<Record<MediaGroupKind, string>> = {
  [MediaGroupKind.Series]: 'series',
  [MediaGroupKind.Line]: 'line',
};

/** Reads `?scope=series:12` or `?scope=line:345`; anything else means the whole franchise. */
export function parseScope(value: unknown): MediaGroupRef | null {
  const raw = Array.isArray(value) ? value[0] : value;
  if (typeof raw !== 'string') return null;
  const match = /^(series|line):(\d+)$/.exec(raw.trim());
  if (!match) return null;
  const id = Number(match[2]);
  if (!Number.isSafeInteger(id) || id <= 0) return null;
  return { kind: match[1] === 'series' ? MediaGroupKind.Series : MediaGroupKind.Line, id };
}

export function formatScope(scope: MediaGroupRef | null | undefined): string | undefined {
  const prefix = scope ? SCOPE_PREFIX[scope.kind] : undefined;
  return prefix ? `${prefix}:${scope!.id}` : undefined;
}

export function franchisePath(franchiseId: number, scope?: MediaGroupRef | null): string {
  const formatted = formatScope(scope);
  return formatted ? `/franchise/${franchiseId}?scope=${formatted}` : `/franchise/${franchiseId}`;
}

const deckIdSet = (franchise: Pick<Franchise, 'nodes'>) => new Set(franchise.nodes.map((n) => n.deckId));

function seriesDeckIds(memberDeckIds: number[], franchiseDeckIds: Set<number>): number[] {
  return [...new Set(memberDeckIds)].filter((id) => franchiseDeckIds.has(id));
}

/** The decks a scope covers, or null when the scope is the whole franchise or names nothing in it. A line is found by any of its decks, not only its anchor. */
export function scopeDeckIds(franchise: Pick<Franchise, 'nodes' | 'series' | 'lines'>, scope: MediaGroupRef | null | undefined): number[] | null {
  if (!scope) return null;
  if (scope.kind === MediaGroupKind.Series) {
    const series = (franchise.series ?? []).find((s) => s.seriesId === scope.id);
    return series ? seriesDeckIds(series.memberDeckIds, deckIdSet(franchise)) : null;
  }
  if (scope.kind === MediaGroupKind.Line) return (franchise.lines ?? []).find((l) => l.deckIds.includes(scope.id))?.deckIds ?? null;
  return null;
}

export function filterNodesByScope<T extends { deckId: number }>(nodes: T[], deckIds: number[] | null): T[] {
  if (!deckIds) return nodes;
  const allowed = new Set(deckIds);
  return nodes.filter((n) => allowed.has(n.deckId));
}

export function filterNodesByMediaTypes<T extends { mediaType: MediaType }>(nodes: T[], mediaTypes: MediaType[]): T[] {
  if (mediaTypes.length === 0) return nodes;
  const allowed = new Set(mediaTypes);
  return nodes.filter((n) => allowed.has(n.mediaType));
}

export interface ScopeChip {
  /** `all`, or the formatted scope. */
  key: string;
  /** Null for the whole franchise. */
  scope: MediaGroupRef | null;
  label: string;
  deckIds: number[];
}

const setKey = (ids: number[]) => [...new Set(ids)].sort((a, b) => a - b).join(',');

/** A chip covering the same decks as an earlier one is dropped, so a series or line spanning the whole franchise never shows. */
export function buildScopeChips(franchise: Pick<Franchise, 'nodes' | 'series' | 'lines'>, localise: Localise): ScopeChip[] {
  const nodeById = new Map(franchise.nodes.map((n) => [n.deckId, n]));
  const chips: ScopeChip[] = [{ key: 'all', scope: null, label: 'Whole franchise', deckIds: franchise.nodes.map((n) => n.deckId) }];
  const seen = new Set([setKey(chips[0]!.deckIds)]);
  const push = (chip: ScopeChip) => {
    const key = setKey(chip.deckIds);
    if (chip.deckIds.length === 0 || seen.has(key)) return;
    seen.add(key);
    chips.push(chip);
  };

  const franchiseDeckIds = deckIdSet(franchise);
  for (const series of franchise.series ?? []) {
    const scope = { kind: MediaGroupKind.Series, id: series.seriesId };
    push({ key: formatScope(scope)!, scope, label: localise(series), deckIds: seriesDeckIds(series.memberDeckIds, franchiseDeckIds) });
  }

  const lines = (franchise.lines ?? [])
    .filter((l) => l.deckIds.length >= 2 && nodeById.has(l.anchorDeckId))
    .sort((a, b) => compareFranchiseRelease(nodeById.get(a.anchorDeckId)!, nodeById.get(b.anchorDeckId)!));
  for (const line of lines) {
    const scope = { kind: MediaGroupKind.Line, id: line.anchorDeckId };
    push({ key: formatScope(scope)!, scope, label: localise(nodeById.get(line.anchorDeckId)!), deckIds: line.deckIds });
  }
  return chips;
}

/** Falls back to the chip covering the same decks, so a scope spanning the whole franchise marks "Whole franchise". */
export function activeScopeChip(chips: ScopeChip[], scope: MediaGroupRef | null, deckIds: number[] | null): ScopeChip | null {
  if (!scope || !deckIds) return chips[0] ?? null;
  const key = formatScope(scope);
  const own = chips.find((c) => c.key === key);
  if (own) return own;
  const target = setKey(deckIds);
  return chips.find((c) => setKey(c.deckIds) === target) ?? null;
}

/** Lines read as series: to users, any named part of a franchise is a series. */
export function mediaGroupKindWord(kind: MediaGroupKind | null | undefined): string {
  return kind === MediaGroupKind.Series || kind === MediaGroupKind.Line ? 'Series' : 'Franchise';
}

interface StoredGroup {
  groupKind?: MediaGroupKind | null;
  groupId?: number | null;
  groupTitles?: MediaGroupTitles | null;
  groupFranchiseId?: number | null;
}

/** Null when the group no longer exists. */
export function mediaGroupName(group: StoredGroup, localise: Localise): string | null {
  return group.groupTitles ? localise(group.groupTitles) : null;
}

export function mediaGroupLabel(group: StoredGroup, localise: Localise): string {
  const kind = mediaGroupKindWord(group.groupKind);
  const name = mediaGroupName(group, localise);
  return name ? `${kind}: ${name}` : `${kind} removed`;
}

export function mediaGroupLink(group: StoredGroup): string | null {
  if (group.groupId == null) return null;
  if (group.groupKind === MediaGroupKind.Franchise) return group.groupTitles ? franchisePath(group.groupId) : null;
  if (group.groupFranchiseId == null) return null;
  return franchisePath(group.groupFranchiseId, { kind: group.groupKind ?? MediaGroupKind.Franchise, id: group.groupId });
}

export function countByMediaType(nodes: { mediaType: MediaType }[]): Map<MediaType, number> {
  const counts = new Map<MediaType, number>();
  for (const n of nodes) counts.set(n.mediaType, (counts.get(n.mediaType) ?? 0) + 1);
  return counts;
}

export function groupFilterSummary(mediaTypes: MediaType[] | null | undefined, excludedDeckIds: number[] | null | undefined): string {
  const types = mediaTypes && mediaTypes.length > 0 ? mediaTypes.map((t) => getMediaTypePluralText(t)).join(', ') : 'All media';
  const excluded = excludedDeckIds?.length ?? 0;
  if (excluded === 0) return types;
  return `${types}, ${excluded} ${excluded === 1 ? 'title' : 'titles'} skipped`;
}
