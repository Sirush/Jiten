import type { FranchiseNode } from './types';
import type { LinkType } from './enums';

export enum MediaGroupKind {
  Franchise = 1,
  Series = 2,
  Line = 3,
}

/** A line is identified by its anchor deck id. */
export interface MediaGroupRef {
  kind: MediaGroupKind;
  id: number;
}

export interface MediaGroupTitles {
  originalTitle: string;
  romajiTitle?: string | null;
  englishTitle?: string | null;
}

/** A story-link component inside a franchise; the anchor is its earliest release. */
export interface FranchiseLine {
  anchorDeckId: number;
  deckIds: number[];
}

export interface MediaGroupMembers {
  kind: MediaGroupKind;
  id: number;
  name: string;
  franchiseId: number | null;
  members: FranchiseNode[];
}

export interface MediaGroupStats {
  characterCount: number;
  uniqueWordCount: number;
  /** 0-5 float, -1 when unknown. */
  difficulty: number;
}

export interface FranchiseSummary {
  franchiseId: number;
  name: string;
  nameIsManual: boolean;
  deckCount: number;
  seriesCount: number;
  /** Any member deck, to open the franchise builder on. */
  firstDeckId: number | null;
  /** Titles of the member deck the name was taken from; null when the name is not a deck title. */
  nameTitles: MediaGroupTitles | null;
}

export type FranchiseListSort = 'name' | 'decks' | 'series';

export interface FranchiseSyncSummary {
  created: number;
  renamed: number;
  merged: number;
  deleted: number;
  unchanged: number;
}

export interface FranchiseSuggestionDeck {
  deck: FranchiseNode;
  franchiseId: number | null;
  franchiseName: string | null;
  linkTypes: LinkType[];
}

/** Decks whose titles share a root, at least one of them outside any franchise. */
export interface FranchiseSuggestion {
  root: string;
  rootKey: string;
  anchorDeckId: number;
  /** Earliest release first. */
  decks: FranchiseSuggestionDeck[];
}

export enum FranchiseSuggestionScope {
  All = 0,
  Franchise = 1,
  Unlinked = 2,
}
