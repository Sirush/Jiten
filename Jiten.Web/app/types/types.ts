import { type ComparisonOutcome, type DeckRelationshipType, type DeckStatus, type FsrsRating, type FsrsState, type Genre, type KnownState, type LinkType, type MediaType, type MediaTypeGroup, type NotificationType, type ReadingType, type RequestAction, type RequestStatus, type WordSetStateType } from '~/types';

export interface Deck {
  deckId: number;
  creationDate: Date;
  releaseDate: Date;
  coverName?: string;
  mediaType: MediaType;
  originalTitle: string;
  romajiTitle?: string;
  englishTitle?: string;
  description?: string;
  characterCount: number;
  wordCount: number;
  uniqueWordCount: number;
  uniqueWordUsedOnceCount: number;
  uniqueKanjiCount: number;
  uniqueKanjiUsedOnceCount: number;
  difficulty: number;
  difficultyRaw: number;
  difficultyOverride: number;
  difficultyAlgorithmic: number;
  averageSentenceLength: number;
  speechDuration: number;
  speechMoraCount: number;
  speechSpeed: number;
  parentDeckId: number;
  deckWords: DeckWord[];
  links: Link[];
  aliases: string[];
  childrenDeckCount: number;
  selectedWordOccurrences: number;
  dialoguePercentage: number;
  coverage: number;
  uniqueCoverage: number;
  youngCoverage: number;
  youngUniqueCoverage: number;
  hideDialoguePercentage: boolean;
  hideAverageSentenceLength: boolean;
  externalRating: number;
  exampleSentence?: ExampleSentence;
  genres?: Genre[];
  tags?: TagWithPercentage[];
  relationships?: DeckRelationship[];
  status?: DeckStatus;
  isFavourite?: boolean;
  isIgnored?: boolean;
  distinctVoterCount: number;
  userAdjustment: number;
}

export interface DeckDetail {
  parentDeck: Deck | null;
  mainDeck: Deck;
  subDecks: Deck[];
}

export interface SimilarDeck {
  deck: Deck;
  similarity: number;
  similarityPercent: number;
}

export interface DeckVocabularyList {
  parentDeck: Deck | null;
  deck: Deck;
  words: DeckWord[];
}

export interface MediaSuggestion {
  deckId: number;
  originalTitle: string;
  romajiTitle?: string;
  englishTitle?: string;
  mediaType: MediaType;
  coverName: string;
}

export interface MediaSuggestionsResponse {
  suggestions: MediaSuggestion[];
  totalCount: number;
}

export interface DeckWord {
  deckId: number;
  originalText: string;
  wordId: number;
  readingType: string;
  readingIndex: number;
  conjugations: string[];
}

export interface ParseNormalisedResult {
  normalisedText: string;
  words: DeckWord[];
}

export interface Link {
  linkId: number;
  url: string;
  linkType: LinkType;
  deckId: number;
}

export interface TagWithPercentage {
  tagId: number;
  name: string;
  percentage: number;
}

export interface DeckRelationship {
  targetDeckId: number;
  targetDeck: Deck;
  relationshipType: DeckRelationshipType;
  isInverse: boolean;
}

export interface MetadataTag {
  name: string;
  percentage: number;
}

export interface Word {
  wordId: number;
  mainReading: Reading;
  alternativeReadings: Reading[];
  partsOfSpeech: string[];
  definitions: Definition[];
  occurrences: number;
  pitchAccents: number[];
  knownStates?: KnownState[];
  composedOf?: WordSummary[];
  usedIn?: WordSummary[];
  usedInTotal?: number;
}

export interface UsedInPage {
  items: WordSummary[];
  total: number;
  page: number;
  pageSize: number;
}

export interface Reading {
  text: string;
  readingType: ReadingType;
  readingIndex: number;
  frequencyRank: number;
  frequencyPercentage: number;
  usedInMediaAmount: number;
  usedInMediaAmountByType: Record<MediaType, number>;
}

export interface Definition {
  index: number;
  meanings: string[];
  partsOfSpeech: string[];
  restrictedToReadingIndices?: number[];
  dial?: string[];
  field?: string[];
}

export interface PaginatedResponse<T> {
  data: T;
  totalItems: number;
  pageSize: number;
  currentOffset: number;
}

export interface GlobalStats {
  mediaByType: Record<MediaType, number>;
  totalMojis: number;
  totalMedia: number;
}

export interface FsrsParametersResponse {
  parameters: string;
  isDefault: boolean;
  desiredRetention: number;
  reviewCount: number;
  minimumReviewsForOptimize: number;
}

export interface SrsRecomputeBatchResponse {
  processed: number;
  total: number;
  lastCardId: number;
  done: boolean;
}

export interface MetadataRelation {
  externalId: string;
  linkType: number;
  relationshipType: number;
  targetMediaType?: number;
  swapDirection: boolean;
}

export interface Metadata {
  originalTitle: string;
  romajiTitle: string;
  englishTitle: string;
  image: string;
  releaseDate: string;
  description: string;
  links: Link[];
  aliases: string[];
  rating: number;
  genres?: string[];
  tags?: MetadataTag[];
  isAdultOnly?: boolean;
  isNotOriginallyJapanese?: boolean;
  relations?: MetadataRelation[];
  dictionaryEntries?: { surface: string }[];
}

export interface Issues {
  missingRomajiTitles: number[];
  missingLinks: number[];
  zeroCharacters: number[];
  missingReleaseDate: number[];
  missingDescription: number[];
  missingGenres: number[];
  missingTags: number[];
}

export interface WordFormSummary {
  readingIndex: number;
  text: string;
  rubyText: string;
  formType: number;
}

export interface MissingFuriganaItem {
  wordId: number;
  readingIndex: number;
  text: string;
  rubyText: string;
  formType: number;
  partsOfSpeech: string[];
  allForms: WordFormSummary[];
  frequencyRank: number | null;
}

export interface MissingFuriganaPaginatedResponse {
  items: MissingFuriganaItem[];
  totalCount: number;
}

export interface WordFormsResponse {
  wordId: number;
  partsOfSpeech: string[];
  forms: WordFormSummary[];
}

export interface LoginRequest {
  usernameOrEmail: string;
  password: string;
}

export interface TokenResponse {
  accessToken: string;
  accessTokenExpiration: Date;
  refreshToken: string;
}

export interface ExampleSentence {
  sentenceId: number;
  text: string;
  wordPosition: number;
  wordLength: number;
  difficulty: number;
  sourceDeck: Deck;
  sourceDeckParent: Deck;
}

export interface UserExampleSentenceDto {
  userExampleSentenceId: number;
  text: string;
  source?: string;
  sortOrder: number;
}

export interface ExampleSentencesByDifficultyResponse {
  minDifficulty: number;
  maxDifficulty: number;
  searchedBandMin: number;
  searchedBandMax: number;
  sentences: ExampleSentence[];
}

export interface GoogleSignInResponse {
  requiresRegistration?: boolean;
  tempToken?: string;
  email?: string;
  name?: string;
  picture?: string;
  accessToken?: string;
  refreshToken?: string;
}

export interface GoogleRegistrationData {
  tempToken: string;
  email: string;
  name: string;
  picture?: string;
  username: string;
}

export interface CompleteGoogleRegistrationRequest {
  tempToken: string;
  username: string;
  tosAccepted: boolean;
  receiveNewsletter: boolean;
}

export interface UserMetadata {
  coverageRefreshedAt?: Date;
}

export interface ApiKeyInfo {
  id: number;
  createdAt: string;
  lastUsedAt?: string;
  expiresAt?: string;
  isRevoked: boolean;
  keyPreview: string;
}

export interface CreateApiKeyResponse {
  apiKey: string;
  id: number;
  message: string;
}

export interface FsrsReviewLogExportDto {
  rating: FsrsRating;
  reviewDateTime: Date;
  reviewDuration?: number;
}

export interface FsrsCardExportDto {
  wordId: number;
  readingIndex: number;
  state: FsrsState;
  step?: number;
  stability?: number;
  difficulty?: number;
  due: Date;
  lastReview?: Date;
  reviewLogs: FsrsReviewLogExportDto[];
}

export interface FsrsExportDto {
  exportDate: Date;
  userId: string;
  totalCards: number;
  totalReviews: number;
  cards: FsrsCardExportDto[];
}

export interface FsrsCardWithWordDto {
  cardId: number;
  wordId: number;
  readingIndex: number;
  state: FsrsState;
  step?: number;
  stability?: number;
  difficulty?: number;
  due: Date;
  lastReview?: Date;
  createdAt: Date;
  lapses: number;
  wordText: string;
  readingType: ReadingType;
  frequencyRank: number;
  wordTextPlain?: string;
}

export interface FsrsImportResultDto {
  cardsImported: number;
  cardsSkipped: number;
  cardsUpdated: number;
  reviewLogsImported: number;
  validationErrors: string[];
}

export interface Tag {
  tagId: number;
  name: string;
}

export interface TagUsage {
  deckCount: number;
  mappingCount: number;
}

export interface GenreMapping {
  externalGenreMappingId: number;
  provider: LinkType;
  providerName: string;
  externalGenreName: string;
  jitenGenre: Genre;
  jitenGenreName: string;
}

export interface TagMapping {
  externalTagMappingId: number;
  provider: LinkType;
  providerName: string;
  externalTagName: string;
  tagId: number;
  tagName: string;
}

export interface TagMappingSummary {
  totalMappings: number;
  mappingsByProvider: Record<string, number>;
}

export interface DeckCoverageStats {
  deckId: number;
  totalUniqueWords: number;
  computedAt: Date;
  rSquared: number;
  milestones: Record<string, number>;
}

export interface CurveDatum {
  rank: number;
  coverage: number;
}

export interface UserProfile {
  userId: string;
  username: string;
  isPublic: boolean;
  isMediaListPublic: boolean;
}

export interface UserAccomplishment {
  accomplishmentId: number;
  userId: string;
  mediaType: MediaType | null;
  completedDeckCount: number;
  totalCharacterCount: number;
  totalWordCount: number;
  uniqueWordCount: number;
  uniqueWordUsedOnceCount: number;
  uniqueKanjiCount: number;
  lastComputedAt: string;
}

export interface AccomplishmentVocabularyDto {
  words: Word[];
}

export interface KanjiReadingWords {
  reading: string;
  totalWords: number;
  words: WordSummary[];
}

export interface Kanji {
  character: string;
  onReadings: string[];
  kunReadings: string[];
  meanings: string[];
  strokeCount: number;
  jlptLevel: number | null;
  grade: number | null;
  frequencyRank: number | null;
  topWords?: WordSummary[];
  wordsByReading?: KanjiReadingWords[];
}

export interface KanjiList {
  character: string;
  meanings: string[];
  strokeCount: number;
  jlptLevel: number | null;
  frequencyRank: number | null;
}

export interface WordSummary {
  wordId: number;
  readingIndex: number;
  reading: string;
  readingFurigana: string;
  mainDefinition: string | null;
  frequencyRank: number | null;
  matchSurface?: string | null;
}

export interface KanjiGridReading {
  reading: string;
  known: number;
  required: number;
  weight: number;
}

export interface KanjiGridItem {
  character: string;
  frequencyRank: number | null;
  jlptLevel: number | null;
  grade: number | null;
  strokeCount: number;
  score: number;
  wordCount: number;
  readings: KanjiGridReading[] | null;
}

export interface KanjiGridResponse {
  kanji: KanjiGridItem[];
  totalKanjiCount: number;
  seenKanjiCount: number;
  lastComputedAt: string | null;
}

export interface ProgressionSegmentDto {
  segment: number;
  difficulty: number;
  peak: number;
  childStartOrder?: number;
  childEndOrder?: number;
}

export interface DeckDifficultyDto {
  difficulty: number;
  peak: number;
  deciles: Record<string, number>;
  progression: ProgressionSegmentDto[];
  lastUpdated: Date;
  distinctVoterCount: number;
  userAdjustment: number;
}

// WordSet types
export interface WordSetDto {
  setId: number;
  slug: string;
  name: string;
  description?: string;
  wordCount: number;
  formCount: number;
}

export interface UserWordSetSubscriptionDto {
  setId: number;
  slug: string;
  name: string;
  description?: string;
  state: WordSetStateType;
  wordCount: number;
  formCount: number;
  subscribedAt: string;
}

export interface WordSetSubscribeRequest {
  state: WordSetStateType;
}

export interface DictionaryEntry {
  wordId: number;
  readingIndex: number;
  text: string;
  rubyText: string;
  primaryKanjiText?: string;
  partsOfSpeech: string[];
  meanings: string[];
  frequencyRank: number;
}

export interface StaticDeckWordDto extends DictionaryEntry {
  occurrences: number;
  deckSortOrder: number;
}

export interface StaticDeckWordsResponse {
  deckName: string;
  words: StaticDeckWordDto[];
}

export interface DictionarySearchResult {
  query: string;
  queryType: string;
  results: DictionaryEntry[];
  dictionaryResults: DictionaryEntry[];
  hasMore: boolean;
}

// Media Request types
export interface MediaRequestDto {
  id: number;
  title: string;
  mediaType: MediaType;
  externalUrl?: string;
  externalLinkType?: LinkType;
  description?: string;
  status: RequestStatus;
  adminNote?: string;
  fulfilledDeckId?: number;
  fulfilledDeckTitle?: string;
  upvoteCount: number;
  commentCount: number;
  uploadCount: number;
  hasUserUpvoted: boolean;
  isSubscribed: boolean;
  isOwnRequest: boolean;
  requesterName?: string;
  createdAt: string;
  completedAt?: string;
}

export interface MediaRequestCommentDto {
  id: number;
  text?: string;
  role: 'Requester' | 'Contributor';
  isOwnComment: boolean;
  userName?: string;
  upload?: MediaRequestUploadDto;
  createdAt: string;
  updatedAt?: string;
}

export interface MediaRequestUploadDto {
  id: number;
  fileName: string;
  fileSize: number;
  originalFileCount: number;
  createdAt: string;
}

export interface MediaRequestUploadAdminDto extends MediaRequestUploadDto {
  uploaderName?: string;
  adminReviewed: boolean;
  adminNote?: string;
  fileDeleted: boolean;
}

export interface DuplicateCheckResultDto {
  existingDecks: DuplicateCheckDeckDto[];
  existingRequests: DuplicateCheckRequestDto[];
}

export interface DuplicateCheckDeckDto {
  deckId: number;
  title: string;
  mediaType: MediaType;
}

export interface DuplicateCheckRequestDto {
  id: number;
  title: string;
  mediaType: MediaType;
  status: RequestStatus;
  upvoteCount: number;
}

export interface RequestActivityLogDto {
  id: number;
  mediaRequestId?: number;
  requestTitle?: string;
  userId: string;
  userName?: string;
  targetUserId?: string;
  action: RequestAction;
  detail?: string;
  createdAt: string;
}

export interface RequestUserSummaryDto {
  requestCount: number;
  upvoteCount: number;
  subscriptionCount: number;
  uploadCount: number;
  fulfilledCount: number;
}

export interface NotificationDto {
  id: number;
  type: NotificationType;
  title: string;
  message: string;
  linkUrl?: string;
  isRead: boolean;
  readAt?: string;
  createdAt: string;
}

export interface DifficultyVoteDto {
  id: number
  deckA: DeckSummaryDto
  deckB: DeckSummaryDto
  outcome: ComparisonOutcome
  createdAt: string
}

export interface DifficultyRatingDto {
  id: number
  deckId: number
  deckTitle: string
  romajiTitle?: string
  englishTitle?: string
  coverUrl: string | null
  mediaType: MediaType
  rating: number
  createdAt: string
}

export interface DeckSummaryDto {
  id: number
  title: string
  romajiTitle?: string
  englishTitle?: string
  coverUrl: string
  difficulty: number
  mediaType: MediaType
}

export interface ComparisonSuggestionDto {
  deckA: DeckSummaryDto
  deckB: DeckSummaryDto
}

export interface VotingStatsDto {
  totalComparisons: number
  totalRatings: number
  percentile: number | null
}

export interface CompletedDecksResponse {
  decks: DeckSummaryDto[]
  votedPairs: number[][]
}

export interface BlacklistedDeckDto {
  deckId: number
  title: string
  romajiTitle?: string
  englishTitle?: string
  coverUrl: string | null
  mediaType: MediaType
  createdAt: string
}

export interface DifficultyRankGroupDto {
  id: number
  sortIndex: number
  decks: DeckSummaryDto[]
}

export interface DifficultyRankingSectionDto {
  group: MediaTypeGroup
  groups: DifficultyRankGroupDto[]
  unranked: DeckSummaryDto[]
}

// SRS Study types
export interface StudyDeckDto {
  userStudyDeckId: number;
  deckType: StudyDeckType;
  name: string;
  description?: string;
  deckId?: number;
  title: string;
  romajiTitle?: string;
  englishTitle?: string;
  coverName?: string;
  mediaType: MediaType;
  sortOrder: number;
  isActive: boolean;
  downloadType: number;
  order: number;
  minFrequency: number;
  maxFrequency: number;
  targetPercentage?: number;
  startFromKnown: boolean;
  minOccurrences?: number;
  maxOccurrences?: number;
  excludeKana: boolean;
  minGlobalFrequency?: number;
  maxGlobalFrequency?: number;
  posFilter?: string;
  totalWords: number;
  unseenCount: number;
  learningCount: number;
  reviewCount: number;
  youngCount: number;
  matureCount: number;
  masteredCount: number;
  blacklistedCount: number;
  suspendedCount: number;
  dueReviewCount: number;
  warning?: string;
  parentDeckId?: number;
  parentTitle?: string;
  parentRomajiTitle?: string;
  parentEnglishTitle?: string;
  parentCoverName?: string;
}

export type StudyMoreMode = 'extraNew' | 'extraReview' | 'ahead' | 'mistakes';

export interface StudyMoreParams {
  mode: StudyMoreMode;
  extraNewCards?: number;
  extraReviews?: number;
  aheadMinutes?: number;
  mistakeDays?: number;
}

export interface StudyBatchResponse {
  sessionId: string;
  cards: StudyCardDto[];
  newCardsRemaining: number;
  reviewsRemaining: number;
  newCardsToday: number;
  reviewsToday: number;
}

export interface StudyCardDto {
  cardId: number;
  wordId: number;
  readingIndex: number;
  state: number;
  isNewCard: boolean;
  wordText: string;
  wordTextPlain: string;
  readings: StudyReadingDto[];
  definitions: StudyDefinitionDto[];
  partsOfSpeech: string[];
  pitchAccents?: number[];
  frequencyRank: number;
  exampleSentence?: StudyExampleSentenceDto;
  intervalPreview?: IntervalPreviewDto;
  deckOccurrences?: StudyDeckOccurrenceDto[];
  sourceDeckName?: string;
  confusableReadings?: string[];
}

export interface StudyDeckOccurrenceDto {
  deckId: number;
  originalTitle: string;
  romajiTitle?: string;
  englishTitle?: string;
  occurrences: number;
  parentOriginalTitle?: string;
  parentRomajiTitle?: string;
  parentEnglishTitle?: string;
}

export interface IntervalPreviewDto {
  againSeconds: number;
  hardSeconds: number;
  goodSeconds: number;
  easySeconds: number;
}

export interface StudyReadingDto {
  text: string;
  rubyText: string;
  readingIndex: number;
  formType: number;
}

export interface StudyDefinitionDto {
  index: number;
  meanings: string[];
  partsOfSpeech: string[];
}

export interface StudyExampleSentenceDto {
  sentenceId: number;
  text: string;
  wordPosition: number;
  wordLength: number;
  sourceDeck?: StudyExampleSourceDto;
  sourceParent?: StudyExampleSourceDto;
  isCustom?: boolean;
  customSource?: string;
  customText?: string;
}

export interface StudyExampleSourceDto {
  deckId: number;
  originalTitle: string;
  romajiTitle?: string;
  englishTitle?: string;
  mediaType: MediaType;
}

export type StudyInterleaving = 'Mixed' | 'NewFirst' | 'ReviewsFirst';
export type StudyNewCardGathering = 'TopDeck' | 'RoundRobin' | 'CrossDeckFrequency';
export type StudyReviewFrom = 'AllTracked' | 'StudyDecksOnly';
export type ExampleSentencePosition = 'Hidden' | 'Back' | 'Front';
export type ExampleSentenceSorting = 'Random' | 'EasiestFirst' | 'HardestFirst';

export interface StudyKeybinds {
  grade1: string;
  grade2: string;
  grade3: string;
  grade4: string;
  flipCard: string;
  blacklist: string;
  forget: string;
  master: string;
  suspend: string;
  bury: string;
  undo: string;
  wrapUp: string;
}

export interface StudySettingsDto {
  newCardsPerDay: number;
  maxReviewsPerDay: number;
  batchSize: number;
  gradingButtons: number;
  interleaving: StudyInterleaving;
  newCardGathering: StudyNewCardGathering;
  reviewFrom: StudyReviewFrom;
  showPitchAccent: boolean;
  exampleSentencePosition: ExampleSentencePosition;
  exampleSentenceSorting: ExampleSentenceSorting;
  blurExampleSentence: boolean;
  showFrequencyRank: boolean;
  showKanjiBreakdown: boolean;
  showWordComposition: boolean;
  showWordUsedIn: boolean;
  showNextInterval: boolean;
  showKeybinds: boolean;
  showElapsedTime: boolean;
  enableSwipeGesture: boolean;
  countFailedReviews: boolean;
  showCardStatus: boolean;
  showFuriganaOnFront: boolean;
  furiganaOnFrontNewOnly: boolean;
  autoPlayWord: boolean;
  autoPlaySentence: boolean;
  autoPlayWordOnFront: boolean;
  autoPlayWordOnFrontNewOnly: boolean;
  autoPlaySentenceOnFront: boolean;
  showReviewActivity: boolean;
  showReviewForecast: boolean;
  timezone: string | null;
  showConfusableReadings: boolean;
  dayBoundaryScheduling: boolean;
  leechThreshold: number;
  leechAction: LeechAction;
  keybinds: StudyKeybinds;
}

export type LeechAction = 'Suspend' | 'NotifyOnly';

export interface CardExamplesResponse {
  examples: Record<string, StudyExampleSentenceDto>;
}

export interface DueSummaryDto {
  reviewsDue: number;
  newCardsAvailable: number;
  reviewsToday: number;
  newCardsToday: number;
  reviewBudgetLeft: number;
  nextReviewAt: string | null;
}

export interface ReviewForecastDto {
  dueWithinHour: number;
  dueToday: number;
  dueTomorrow: number;
  nextReviewAt: string | null;
  dayBoundaryScheduling: boolean;
}

export interface ReviewForecast30dDto {
  days: { date: string; count: number }[];
}

export interface SessionStreakDto {
  currentStreak: number;
  longestStreak: number;
  isNewRecord: boolean;
}

export interface DeckStreakDto {
  currentStreak: number;
  longestStreak: number;
  isNewRecord: boolean;
  totalReviewDays: number;
  recentDays: { date: string; count: number }[];
}

export interface RetentionBucketDto {
  total: number;
  passed: number;
  retention: number | null;
}

export interface RetentionWindowDto {
  overall: RetentionBucketDto;
  young: RetentionBucketDto;
  mature: RetentionBucketDto;
}

export interface PeriodRetentionDto {
  period: string;
  overall: RetentionBucketDto;
  young: RetentionBucketDto;
  mature: RetentionBucketDto;
}

export interface RetentionResponseDto {
  desiredRetention: number;
  matureThresholdDays: number;
  windows: {
    last30: RetentionWindowDto;
    last90: RetentionWindowDto;
    all: RetentionWindowDto;
  };
  weekly: PeriodRetentionDto[];
  monthly: PeriodRetentionDto[];
}

export interface StudyHeatmapResponse {
  year: number;
  days: HeatmapDay[];
  currentStreak: number;
  longestStreak: number;
  totalReviewDays: number;
  totalReviews: number;
}

export interface HeatmapDay {
  date: string;
  reviewCount: number;
  correctCount: number;
}

export interface ReviewHistoryDto {
  card?: {
    state: FsrsState;
    stability?: number;
    difficulty?: number;
    due: string;
    lastReview?: string;
    createdAt: string;
    lapses: number;
  };
  reviews: {
    rating: FsrsRating;
    reviewDateTime: string;
    reviewDuration?: number;
  }[];
}

export interface RecentReviewDto {
  wordId: number;
  readingIndex: number;
  wordText: string;
  rating: FsrsRating;
  reviewDateTime: string;
  reviewDuration?: number;
  cardState: FsrsState;
}

export interface AddStudyDeckRequest {
  deckType: StudyDeckType;
  name?: string;
  description?: string;
  deckId?: number;
  downloadType: number;
  order: number;
  minFrequency: number;
  maxFrequency: number;
  targetPercentage?: number;
  startFromKnown?: boolean;
  minOccurrences?: number;
  maxOccurrences?: number;
  excludeKana: boolean;
  minGlobalFrequency?: number;
  maxGlobalFrequency?: number;
  posFilter?: string;
}

export interface UpdateStudyDeckRequest {
  name?: string;
  description?: string;
  downloadType: number;
  order: number;
  minFrequency: number;
  maxFrequency: number;
  targetPercentage?: number;
  startFromKnown?: boolean;
  minOccurrences?: number;
  maxOccurrences?: number;
  excludeKana: boolean;
  minGlobalFrequency?: number;
  maxGlobalFrequency?: number;
  posFilter?: string;
}

export interface CorpusSnippet {
  html: string;
  deckId: number;
  deckTitle: string;
  mediaType: MediaType;
  difficulty: number;
  releaseYear: number;
}

export interface CorpusMediaBreakdown {
  mediaType: MediaType;
  hits: number;
  deckCount: number;
  totalCharacters: number;
  hitsPerMillion: number;
  percentage: number;
}

export interface CorpusTrendPoint {
  year: number;
  matchingChars: number;
  totalCharsInYear: number;
  percentage: number;
}

export interface CorpusDifficultyBucket {
  bucketMin: number;
  bucketMax: number;
  deckCount: number;
  hits: number;
}

export interface CorpusTopDeck {
  deckId: number;
  title: string;
  parentTitle: string | null;
  mediaType: MediaType;
  occurrences: number;
  perMillion: number;
}

export interface CorpusTermResult {
  term: string;
  totalHits: number;
  matchingDecks: number;
  hitsPerMillion: number;
  snippets: CorpusSnippet[];
  mediaBreakdown: CorpusMediaBreakdown[];
  trends: CorpusTrendPoint[];
  difficultyDistribution: CorpusDifficultyBucket[];
  topDecks: CorpusTopDeck[];
  dialogueWeightedAvg: number;
}

export interface CorpusStats {
  totalDecks: number;
  totalCharacters: number;
  decksWithRawText: number;
}

export interface CorpusSearchResponse {
  results: CorpusTermResult[];
  corpusStats: CorpusStats;
}

export interface CorpusCoOccurrence {
  termA: string;
  termB: string;
  sharedDecks: number;
}
