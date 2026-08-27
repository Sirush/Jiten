import { type Deck, SortOrder } from '~/types';

export interface DeckSortMeta {
  default: SortOrder;
  asc: string;
  desc: string;
}

export const deckSortMeta: Record<string, DeckSortMeta> = {
  title: { default: SortOrder.Ascending, asc: 'A → Z', desc: 'Z → A' },
  difficulty: { default: SortOrder.Ascending, asc: 'Easiest first', desc: 'Hardest first' },
  coverage: { default: SortOrder.Descending, asc: 'Lowest first', desc: 'Highest first' },
  totalCoverage: { default: SortOrder.Descending, asc: 'Lowest first', desc: 'Highest first' },
  uCoverage: { default: SortOrder.Descending, asc: 'Lowest first', desc: 'Highest first' },
  uTotalCoverage: { default: SortOrder.Descending, asc: 'Lowest first', desc: 'Highest first' },
  extRating: { default: SortOrder.Descending, asc: 'Lowest first', desc: 'Highest first' },
  communityVotes: { default: SortOrder.Descending, asc: 'Fewest first', desc: 'Most first' },
  sentenceLength: { default: SortOrder.Ascending, asc: 'Shortest first', desc: 'Longest first' },
  uKanji: { default: SortOrder.Ascending, asc: 'Fewest first', desc: 'Most first' },
  uWordCount: { default: SortOrder.Ascending, asc: 'Fewest first', desc: 'Most first' },
  wordCount: { default: SortOrder.Ascending, asc: 'Fewest first', desc: 'Most first' },
  subdeckCount: { default: SortOrder.Ascending, asc: 'Fewest first', desc: 'Most first' },
  uKanjiOnce: { default: SortOrder.Ascending, asc: 'Fewest first', desc: 'Most first' },
  releaseDate: { default: SortOrder.Descending, asc: 'Oldest first', desc: 'Newest first' },
  addedDate: { default: SortOrder.Descending, asc: 'Oldest first', desc: 'Newest first' },
  charCount: { default: SortOrder.Ascending, asc: 'Shortest first', desc: 'Longest first' },
  dialoguePercentage: { default: SortOrder.Descending, asc: 'Least dialogue', desc: 'Most dialogue' },
  speechSpeed: { default: SortOrder.Ascending, asc: 'Slowest first', desc: 'Fastest first' },
  speechDuration: { default: SortOrder.Descending, asc: 'Shortest first', desc: 'Longest first' },
  occurrences: { default: SortOrder.Descending, asc: 'Fewest first', desc: 'Most first' },
  filter: { default: SortOrder.Descending, asc: 'Least relevant', desc: 'Most relevant' },
};

export const deckSortLabels: Record<string, string> = {
  title: 'Title',
  difficulty: 'Difficulty',
  totalCoverage: 'Coverage (Total)',
  coverage: 'Coverage (Mature)',
  uTotalCoverage: 'Unique Coverage (Total)',
  uCoverage: 'Unique Coverage (Mature)',
  extRating: 'External Rating',
  sentenceLength: 'Average Sentence Length',
  uKanji: 'Unique Kanji',
  uWordCount: 'Unique Word Count',
  wordCount: 'Word Count',
  subdeckCount: 'Subdeck Count',
  uKanjiOnce: 'Unique Kanji Used Once',
  communityVotes: 'Community Ratings',
  releaseDate: 'Release Date',
  addedDate: 'Added Date',
  charCount: 'Character Count',
  dialoguePercentage: 'Dialogue Percentage',
  speechSpeed: 'Speech Speed',
  speechDuration: 'Speech Duration',
  occurrences: 'Occurrences',
};

// Display order of the General group, so options staying consistent as entries are added or removed.
export const deckSortOrdering = [
  'title',
  'difficulty',
  'totalCoverage',
  'coverage',
  'uTotalCoverage',
  'uCoverage',
  'extRating',
  'sentenceLength',
  'uKanji',
  'uWordCount',
  'wordCount',
  'subdeckCount',
  'uKanjiOnce',
  'communityVotes',
  'releaseDate',
  'addedDate',
];

export interface DeckSortOption {
  label: string;
  value: string;
}

export function deckSortOption(key: string): DeckSortOption {
  return { label: deckSortLabels[key] ?? key, value: key };
}

const deckSortValues: Record<string, (deck: Deck) => number | string> = {
  title: (d) => d.originalTitle ?? '',
  difficulty: (d) => d.difficultyRaw,
  charCount: (d) => d.characterCount,
  wordCount: (d) => d.wordCount,
  uWordCount: (d) => d.uniqueWordCount,
  uKanji: (d) => d.uniqueKanjiCount,
  uKanjiOnce: (d) => d.uniqueKanjiUsedOnceCount,
  subdeckCount: (d) => d.childrenDeckCount,
  extRating: (d) => d.externalRating,
  communityVotes: (d) => d.distinctVoterCount,
  releaseDate: (d) => new Date(d.releaseDate).getTime(),
  addedDate: (d) => new Date(d.creationDate).getTime(),
  sentenceLength: (d) => d.averageSentenceLength,
  dialoguePercentage: (d) => d.dialoguePercentage,
  speechSpeed: (d) => d.speechSpeed,
  speechDuration: (d) => d.speechDuration,
  coverage: (d) => d.coverage,
  uCoverage: (d) => d.uniqueCoverage,
  totalCoverage: (d) => Math.min(d.coverage + d.youngCoverage, 100),
  uTotalCoverage: (d) => Math.min(d.uniqueCoverage + d.youngUniqueCoverage, 100),
};

const UNSET_RELEASE_DATE_CUTOFF = new Date('1900-01-01').getTime();

// Decks with no value for the sort key stay at the bottom whichever direction is picked, so
// flipping the order never fills the top of the list with blanks.
function isMissing(deck: Deck, key: string): boolean {
  switch (key) {
    case 'releaseDate':
      return new Date(deck.releaseDate).getTime() < UNSET_RELEASE_DATE_CUTOFF;
    case 'dialoguePercentage':
      return deck.hideDialoguePercentage;
    case 'sentenceLength':
      return deck.hideAverageSentenceLength;
    default:
      return false;
  }
}

export function sortDecks(decks: Deck[], key: string, order: SortOrder): Deck[] {
  const value = deckSortValues[key];
  if (!value) return decks;

  const direction = order === SortOrder.Ascending ? 1 : -1;

  return [...decks].sort((a, b) => {
    const missingA = isMissing(a, key);
    const missingB = isMissing(b, key);
    if (missingA !== missingB) return missingA ? 1 : -1;

    const va = value(a);
    const vb = value(b);
    const primary = typeof va === 'string' ? va.localeCompare(vb as string, 'ja') : va - (vb as number);
    if (primary !== 0) return primary * direction;

    return (a.originalTitle ?? '').localeCompare(b.originalTitle ?? '', 'ja');
  });
}
