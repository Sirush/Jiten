import type { FranchiseNode } from './types';

export enum SeriesKind {
  Series = 1,
  Setting = 2,
}

export interface SeriesRef {
  seriesId: number;
  name: string;
  kind: SeriesKind;
}

export interface SeriesSummary extends SeriesRef {
  deckCount: number;
}

export interface SeriesDetail {
  seriesId: number;
  name: string;
  kind: SeriesKind;
  /** Franchise holding most of the members; null for a setting or when no member has one. */
  franchiseId: number | null;
  members: FranchiseNode[];
}

export interface FranchiseSeries {
  seriesId: number;
  name: string;
  memberDeckIds: number[];
}

/** A setting shared by franchise decks; settings never merge franchises. */
export interface FranchiseSetting {
  seriesId: number;
  name: string;
  /** Members that are part of this franchise. */
  memberDeckIds: number[];
  /** Members outside this franchise (capped at 20). */
  outside: FranchiseNode[];
  outsideCount: number;
}

export type FranchiseViewKind = 'timeline' | 'series' | 'web';
