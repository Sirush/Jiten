import type { FranchiseNode } from './types';
import type { MediaGroupTitles } from './mediaGroup';

export enum SeriesKind {
  Series = 1,
  Setting = 2,
}

export interface SeriesRef extends MediaGroupTitles {
  seriesId: number;
  kind: SeriesKind;
}

export interface SeriesSummary extends SeriesRef {
  deckCount: number;
}

export interface SeriesDetail extends MediaGroupTitles {
  seriesId: number;
  kind: SeriesKind;
  /** Franchise holding most of the members; null for a setting or when no member has one. */
  franchiseId: number | null;
  members: FranchiseNode[];
}

export interface FranchiseSeries extends MediaGroupTitles {
  seriesId: number;
  memberDeckIds: number[];
}

/** A setting shared by franchise decks; settings never merge franchises. */
export interface FranchiseSetting extends MediaGroupTitles {
  seriesId: number;
  /** Members that are part of this franchise. */
  memberDeckIds: number[];
  /** Members outside this franchise (capped at 20). */
  outside: FranchiseNode[];
  outsideCount: number;
}

export type FranchiseViewKind = 'timeline' | 'series' | 'web';
