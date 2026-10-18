import type { MediaType } from './enums';

export interface SiteStatsMonthPoint {
  month: string;
  total: number;
}

export interface SiteStatsTotals {
  learners: number;
  reviews: number;
  wordsKnown: number;
  hoursReviewing: number;
  titlesFinished: number;
  charactersRead: number;
}

export interface SiteStatsRecent {
  peopleReviewing: number;
  reviews: number;
  newWords: number;
  medianReviewsPerStudyDay: number;
}

export interface SiteStatsHeatmap {
  reviews: number[][];
  learners: number;
}

export interface SiteStatsAtLeast {
  words: number;
  share: number;
}

export interface SiteStatsKnownWords {
  learners: number;
  median: number;
  p75: number;
  atLeast: SiteStatsAtLeast[];
}

export interface SiteStatsFrequencyBand {
  from: number;
  to: number;
  p25: number;
  median: number;
  p75: number;
  p90: number;
}

export interface SiteStatsWordsForCoverageType {
  mediaType: MediaType;
  titles: number;
  ranks: (number | null)[];
  libraryWords?: (number | null)[] | null;
  excludedPercent: number;
}

export interface SiteStatsWordsForCoverage {
  computedAt: string;
  levels: number[];
  mediaTypes: SiteStatsWordsForCoverageType[];
}

export interface SiteStatsReading {
  mediaType: MediaType | null;
  learners: number;
  characters: number;
  titlesFinished: number;
  unitsFinished: number;
}

export interface SiteStatsStudiedDeck {
  deckId: number;
  mediaType: MediaType;
  originalTitle: string;
  romajiTitle?: string | null;
  englishTitle?: string | null;
  coverName: string;
  learners: number;
}

export interface SiteStatsForgottenWord {
  wordId: number;
  readingIndex: number;
  text: string;
  readingFurigana: string;
  gloss?: string | null;
  missedPercent: number;
}

export interface SiteStatsLibrary {
  titles: number;
  charactersParsed: number;
  addedThisMonth: number;
  requestsFulfilled: number;
  difficultyVotes: number;
  byMonth: SiteStatsMonthPoint[];
}

export interface SiteStats {
  date: string;
  computedAt: string;
  totals: SiteStatsTotals;
  recent?: SiteStatsRecent;
  learnersByMonth: SiteStatsMonthPoint[];
  heatmap?: SiteStatsHeatmap | null;
  knownWords?: SiteStatsKnownWords | null;
  frequencyBands: SiteStatsFrequencyBand[];
  wordsForCoverage?: SiteStatsWordsForCoverage | null;
  reading: SiteStatsReading[];
  studying: SiteStatsStudiedDeck[];
  forgotten: SiteStatsForgottenWord[];
  library: SiteStatsLibrary;
}
