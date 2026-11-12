export type WordCheckLevel = 'Starting' | 'Beginner' | 'Intermediate' | 'Advanced';

export const WORD_CHECK_LEVEL_TITLES: Record<WordCheckLevel, string> = {
  Starting: 'Just starting',
  Beginner: 'Beginner',
  Intermediate: 'Intermediate',
  Advanced: 'Advanced',
};

export type OnboardingPath = 'srs' | 'anki' | 'reader';

export interface WordCheckItem {
  id: number;
  text: string;
  reading: string | null;
  meanings: string[] | null;
}

export interface WordCheckStep {
  sessionId: string;
  phase: 'probe' | 'verify' | 'failed';
  items: WordCheckItem[];
  answered: number;
  estimatedTotal: number;
}

export interface WordCheckResult {
  wordsKnown: number;
  wordsMarked: number;
  batchId: string | null;
}

export interface OnboardingState {
  level: WordCheckLevel | null;
  openedAt: string | null;
  wordCheckDoneAt: string | null;
  wordCheckWords: number | null;
  wordCheckBatchId: string | null;
  readingCheck: ReadingCheckRun | null;
  basicGrammarMissing: number;
  knownWords: number;
  shouldPrompt: boolean;
  showSetupStrip: boolean;
}

export interface ReadingCheckRun {
  finishedAt: string;
  units: number;
  seeded: number;
  retentionEncounters: number | null;
  batchId: string | null;
}
