import type { MediaType } from '~/types';

export interface ReadingUnit {
  deckId: number;
  originalTitle: string;
  romajiTitle: string | null;
  englishTitle: string | null;
  inProgress: boolean;
  ticked: boolean;
}

export interface ReadingWork {
  key: string;
  originalTitle: string;
  romajiTitle: string | null;
  englishTitle: string | null;
  coverName: string | null;
  mediaType: MediaType;
  fromMediaList: boolean;
  units: ReadingUnit[];
}

export interface ReadingCount {
  units: number;
  candidates: number;
}

export interface ReadingItem {
  id: number;
  text: string;
  reading: string;
  meanings: string[];
}

export interface ReadingLevelOption {
  level: number;
  known: number;
  expectedWrong: number;
}

export interface ReadingStep {
  sessionId: string;
  phase: 'probe' | 'fit' | 'verify';
  items: ReadingItem[];
  answered: number;
  estimatedTotal: number;
  levels: ReadingLevelOption[];
  level: number;
  known: number;
}

export interface ReadingStudyWord {
  wordId: number;
  readingIndex: number;
  text: string;
  reading: string;
  meaning: string;
  occurrences: number;
}

export interface ReadingResult {
  seeded: number;
  batchId: string | null;
  retentionEncounters: number | null;
  studyList: ReadingStudyWord[];
}

export const READING_MIN_UNITS = 3;
export const READING_MIN_CANDIDATES = 2000;
