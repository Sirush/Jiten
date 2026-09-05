import type {
  ComparisonOutcome,
  DeckRelationshipType,
  DeckStatus,
  FsrsRating,
  FsrsState,
  Genre,
  KnownState,
  LinkType,
  MediaType,
  MediaTypeGroup,
  NotificationType,
  ReadingType,
  RequestAction,
  RequestKind,
  RequestStatus,
  WordSetStateType,
} from '~/types';

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
  adjustmentConfidence: number;
  originalFileName?: string | null;
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

export interface DescriptionSearchResponse {
  query: string;
  searchedText: string;
  detectedMediaType: MediaType | null;
  mediaType: MediaType | null;
  results: SimilarDeck[];
}

export interface DeckVocabularyList {
  parentDeck: Deck | null;
  deck: Deck;
  words: DeckWord[];
  appliedFrequencySource?: MediaType | null;
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

export interface DeckMetadataPatch {
  originalTitle?: string;
  romajiTitle?: string;
  englishTitle?: string;
  description?: string;
  hideDialoguePercentage?: boolean;
  hideAverageSentenceLength?: boolean;
  genres?: Genre[];
  tags?: { tagId: number; percentage: number }[];
  links?: { linkType: LinkType; url: string }[];
  relationships?: { sourceDeckId: number; targetDeckId: number; relationshipType: DeckRelationshipType }[];
}

export interface DeckMetadataPatchResult {
  originalTitle: string;
  romajiTitle: string;
  englishTitle: string;
  description: string;
  hideDialoguePercentage: boolean;
  hideAverageSentenceLength: boolean;
  genres: Genre[];
  tags: TagWithPercentage[];
  links: Link[];
  relationships: DeckRelationship[];
}

export interface FranchiseNode {
  deckId: number;
  originalTitle: string;
  romajiTitle: string;
  englishTitle: string;
  coverName: string;
  mediaType: MediaType;
  releaseDate: string;
  difficulty: number;
  difficultyRaw: number;
  characterCount: number;
  wordCount: number;
  childrenDeckCount: number;
  coverage: number;
  uniqueCoverage: number;
}

export interface FranchiseEdge {
  sourceDeckId: number;
  targetDeckId: number;
  relationshipType: DeckRelationshipType;
}

export interface Franchise {
  nodes: FranchiseNode[];
  edges: FranchiseEdge[];
  truncated: boolean;
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
  languageSources?: LanguageSource[];
  entryInfo?: string[];
  derivedFrom?: WordDerivationDto[];
  derives?: WordDerivationDto[];
  /** Present only when this form has no card of its own and an enabled derivation covers it. */
  redundantVia?: DerivationCoverDto | null;
}

export interface LanguageSource {
  lang: string;
  text: string;
  isWasei: boolean;
  isPartial: boolean;
}

export interface CrossReference {
  type: string; // see | ant | syn
  targetWordId?: number;
  targetText: string;
  targetKanji?: string;
  targetReading?: string;
  targetSenseIndex?: number;
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
  /** Absent while the caller is on the site-wide ranking. */
  frequencyRankSource?: FrequencyRankSource;
  /** Set only when the chosen media type had no rank and the global one stood in. */
  isFrequencyFallback?: boolean;
}

export type FrequencyRankSource = 'global' | 'mediaType' | 'list';

export interface FrequencyRankEntry {
  rank: number;
  percentage: number;
  amount: number;
}

export interface FrequencyListRank {
  id: number;
  name: string;
  /** 0 means the word is outside the list. */
  rank: number;
}

export interface ResolvedFrequencyRank {
  source: FrequencyRankSource;
  mediaType?: MediaType;
  listId?: number;
  listName?: string;
  rank: number;
  isFallback: boolean;
}

export interface WordFrequencyRanks {
  global: FrequencyRankEntry;
  /** Keyed by media type id; only the types that observed the form appear. */
  byType: Record<string, FrequencyRankEntry>;
  /** Only present for authenticated callers who asked for it. */
  lists?: FrequencyListRank[];
  resolved: ResolvedFrequencyRank;
}

export interface Definition {
  index: number;
  meanings: string[];
  partsOfSpeech: string[];
  restrictedToReadingIndices?: number[];
  dial?: string[];
  field?: string[];
  misc?: string[];
  senseInfo?: string[];
  glossTypes?: string[];
  crossReferences?: CrossReference[];
}

export interface PaginatedResponse<T> {
  data: T;
  totalItems: number;
  pageSize: number;
  currentOffset: number;
}

export interface DeckRankingRow {
  deckId: number;
  originalTitle: string;
  romajiTitle: string;
  englishTitle: string;
  difficulty: number;
  characterCount: number;
  releaseYear: number | null;
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

export interface FsrsHealthResponse {
  totalReviews: number;
  // [Again, Hard, Good, Easy]
  ratingCounts: number[];
  sameDayReviews: number;
  minimumReviewsForOptimize: number;
  meetsMinimum: boolean;
  neverUsesHard: boolean;
  neverUsesEasy: boolean;
  likelyHardAsFail: boolean;
}

export interface WorkloadCurvePoint {
  retention: number;
  reviewsPerDay: number;
  minutesPerDay: number;
  recallPct: number;
  multiplier: number;
}

export interface FsrsWorkloadCurveResponse {
  baseRetention: number;
  horizonDays: number;
  includeNewCards: boolean;
  sampled: number;
  total: number;
  learningSeconds: number;
  youngSeconds: number;
  matureSeconds: number;
  points: WorkloadCurvePoint[];
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

export interface AccountInfo {
  userId: string;
  userName: string;
  email: string;
  emailConfirmed: boolean;
  hasPassword: boolean;
  createdAt: string;
  receivesNewsletter: boolean;
  rateLimitTier: string;
  roles: string[];
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface SetPasswordRequest {
  newPassword: string;
}

export interface ChangeEmailRequest {
  newEmail: string;
  currentPassword?: string;
}

export interface ConfirmEmailChangeRequest {
  userId: string;
  newEmail: string;
  code: string;
}

export interface ResendConfirmationRequest {
  email: string;
  recaptchaResponse: string;
}

export interface UpdateAccountPreferencesRequest {
  receivesNewsletter: boolean;
}

export interface ExampleSentence {
  sentenceId: number;
  text: string;
  wordPosition: number;
  wordLength: number;
  difficulty: number;
  sourceDeck: StudyExampleSourceDto;
  sourceDeckParent?: StudyExampleSourceDto;
  fromStudyDeck?: boolean;
}

export interface UserExampleSentenceDto {
  userExampleSentenceId: number;
  text: string;
  source?: string;
  sortOrder: number;
}

export interface UserCustomMeaningDto {
  wordId: number;
  text: string;
}

export interface UserHiddenDefinitionsDto {
  wordId: number;
  hiddenIndices: number[];
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

export interface UserExampleSentenceExportDto {
  w: number;
  r: number;
  s: string;
  src?: string;
  o: number;
  ca: number;
}

export interface UserCustomMeaningExportDto {
  w: number;
  m: string;
  ca: number;
  ua: number;
}

export interface FsrsExportDto {
  exportDate: Date;
  userId: string;
  totalCards: number;
  totalReviews: number;
  cards: FsrsCardExportDto[];
  customSentences?: UserExampleSentenceExportDto[];
  customMeanings?: UserCustomMeaningExportDto[];
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
  customSentencesImported: number;
  customSentencesSkipped: number;
  customMeaningsImported: number;
  customMeaningsSkipped: number;
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
  completedUnitCount: number;
  totalCharacterCount: number;
  totalWordCount: number;
  uniqueWordCount: number;
  uniqueWordUsedOnceCount: number;
  uniqueKanjiCount: number;
  lastComputedAt: string;
}

export interface ProfileVocabularyStats {
  young: number;
  mature: number;
  mastered: number;
  wordSetMastered: number;
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
  grade: number | null;
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

export interface DeckVocabularyPreviewWord {
  wordId: number;
  readingIndex: number;
  reading: string;
  readingFurigana: string;
  mainDefinition: string | null;
  frequencyRank: number | null;
  occurrences: number;
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
  adjustmentConfidence: number;
}

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

export interface MediaRequestDto {
  id: number;
  title: string;
  kind: RequestKind;
  mediaType: MediaType;
  externalUrl?: string;
  externalLinkType?: LinkType;
  description?: string;
  status: RequestStatus;
  adminNote?: string;
  targetDeckId?: number;
  targetDeckTitle?: string;
  fulfilledDeckId?: number;
  fulfilledDeckTitle?: string;
  upvoteCount: number;
  boostCount: number;
  commentCount: number;
  uploadCount: number;
  hasUserUpvoted: boolean;
  hasUserBoosted: boolean;
  isSubscribed: boolean;
  isOwnRequest: boolean;
  requesterName?: string;
  createdAt: string;
  completedAt?: string;
}

export interface MediaRequestCommentDto {
  id: number;
  text?: string;
  role: 'Requester' | 'Contributor' | 'Admin';
  isOwnComment: boolean;
  isAdminComment?: boolean;
  userName?: string;
  upload?: MediaRequestUploadDto;
  createdAt: string;
  updatedAt?: string;
  adminComments?: MediaRequestCommentDto[];
}

export interface MediaRequestUploadDto {
  id: number;
  fileSize: number;
  originalFileCount: number;
  createdAt: string;
}

export interface MediaRequestUploadAdminDto extends MediaRequestUploadDto {
  fileName: string;
  uploaderName?: string;
  adminReviewed: boolean;
  adminNote?: string;
  fileDeleted: boolean;
}

export interface DuplicateCheckResultDto {
  existingDecks: DuplicateCheckDeckDto[];
  existingRequests: DuplicateCheckRequestDto[];
  existingUpdateRequests: DuplicateCheckRequestDto[];
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
  boostCount: number;
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
  id: number;
  deckA: DeckSummaryDto;
  deckB: DeckSummaryDto;
  outcome: ComparisonOutcome;
  createdAt: string;
}

export interface DifficultyRatingDto {
  id: number;
  deckId: number;
  deckTitle: string;
  romajiTitle?: string;
  englishTitle?: string;
  coverUrl: string | null;
  mediaType: MediaType;
  rating: number;
  createdAt: string;
}

export interface DeckSummaryDto {
  id: number;
  title: string;
  romajiTitle?: string;
  englishTitle?: string;
  coverUrl: string;
  difficulty: number;
  mediaType: MediaType;
}

export interface ComparisonSuggestionDto {
  deckA: DeckSummaryDto;
  deckB: DeckSummaryDto;
}

export interface VotingStatsDto {
  totalComparisons: number;
  totalRatings: number;
  percentile: number | null;
}

export interface CompletedDecksResponse {
  decks: DeckSummaryDto[];
  votedPairs: number[][];
}

export interface BlacklistedDeckDto {
  deckId: number;
  title: string;
  romajiTitle?: string;
  englishTitle?: string;
  coverUrl: string | null;
  mediaType: MediaType;
  createdAt: string;
}

export interface DifficultyRankGroupDto {
  id: number;
  sortIndex: number;
  decks: DeckSummaryDto[];
}

export interface DifficultyRankingSectionDto {
  group: MediaTypeGroup;
  groups: DifficultyRankGroupDto[];
  unranked: DeckSummaryDto[];
}

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
  frequencyMediaType?: MediaType;
  frequencyListId?: number;
  frequencySourceName?: string;
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
  due?: string | null;
  lapses: number;
  isLeech: boolean;
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
// What the question-side timer does when it expires.
// Reveal = flip to the answer; FailLearn = reveal + lock + auto-fail after a beat; Nudge = alert only.
export type TimedRevealAction = 'Reveal' | 'FailLearn' | 'Nudge';
// What the answer-side timer does when it expires.
// SoftFail = arm Again with an overridable grace; HardFail = grade Again immediately.
export type TimedAnswerAction = 'SoftFail' | 'HardFail';
export type ExampleSentencePosition = 'Hidden' | 'Back' | 'Front';

export type CardImageLayout = 'beside' | 'below';
export type CardImagePosition = 'Front' | 'Back';
export type CardAudioAutoPlayPosition = 'Front' | 'Back' | 'Both';
export type ExampleSentenceSorting = 'Random' | 'EasiestFirst' | 'HardestFirst';
export type ExampleSentenceSource = 'StudyDecks' | 'Random';

/** "Speed Focus" timed-review preferences. Behaviour is entirely client-side; the server round-trips it. */
export interface TimedReviewSettings {
  enabled: boolean;
  showTimer: boolean;
  skipNewCards: boolean;
  revealEnabled: boolean;
  revealSeconds: number;
  revealAction: TimedRevealAction;
  answerEnabled: boolean;
  answerSeconds: number;
  answerAction: TimedAnswerAction;
  alertSound: boolean;
}

// Write-in review: what happens when the typed answer is wrong.
// Reveal = flip to the answer and suggest Again; Retry = shake the field and let the user try again or give up.
export type WriteInWrongBehavior = 'Reveal' | 'Retry';

/** "Write-in review" preferences. Behaviour is entirely client-side; the server round-trips it. */
export interface WriteInReviewSettings {
  modalitySrs: boolean;
  modalityReading: boolean;
  modalityMeaning: boolean;
  inlineInput: boolean;
  wrongBehavior: WriteInWrongBehavior;
  romajiInput: boolean;
  meaningShowReading: boolean;
  skipNewCards: boolean;
  autoAdvance: boolean;
  autoAdvanceWrong: boolean;
  autoAdvanceSeconds: number;
  sound: boolean;
  timed: boolean;
}

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
  pauseTimer: string;
  replayAudio: string;
  dictPrev: string;
  dictNext: string;
}

export interface StudySettingsDto {
  newCardsPerDay: number;
  maxReviewsPerDay: number;
  batchSize: number;
  pauseBetweenBatches: boolean;
  gradingButtons: number;
  interleaving: StudyInterleaving;
  newCardGathering: StudyNewCardGathering;
  reviewFrom: StudyReviewFrom;
  showPitchAccent: boolean;
  exampleSentencePosition: ExampleSentencePosition;
  exampleSentenceSorting: ExampleSentenceSorting;
  exampleSentenceSource: ExampleSentenceSource;
  blurExampleSentence: boolean;
  cardImageLayout: CardImageLayout;
  cardImagePosition: CardImagePosition;
  blurCardImage: boolean;
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
  autoPlayCustomAudio: boolean;
  autoPlayCustomAudioPosition: CardAudioAutoPlayPosition;
  customAudioReplacesHeadword: boolean;
  customAudioReplacesSentence: boolean;
  /** Below the server's current version the custom-audio fields are overwritten with the current defaults. */
  audioDefaultsVersion: number;
  showReviewActivity: boolean;
  showReviewForecast: boolean;
  timezone: string | null;
  showConfusableReadings: boolean;
  dayBoundaryScheduling: boolean;
  loadBalancing: boolean;
  /** Per-weekday Easy Days load weights, index 0=Sunday…6=Saturday, each in [0,1]. Null = off. */
  easyDays: number[] | null;
  /** Derivation category keys treated as redundant. Empty = feature off; omitted on a save = leave unchanged. */
  derivationalRedundancyCategories?: string[] | null;
  /** Media type whose ranking replaces the global one everywhere. Omitted on a save = unchanged; 0 = back to global. */
  defaultFrequencyMediaType?: number | null;
  /** Custom frequency list whose ranking replaces the global one. Same omitted/0 rules as the media type. */
  defaultFrequencyListId?: number | null;
  leechThreshold: number;
  leechAction: LeechAction;
  timedReview: TimedReviewSettings;
  writeInReview: WriteInReviewSettings;
  keybinds: StudyKeybinds;
  // Null/absent = derive the card layout from the legacy display toggles above. Once the layout editor
  // writes an explicit layout it takes precedence and those toggles no longer drive the card display.
  cardLayout?: CardLayout | null;
  cardLayoutPresets?: CardLayoutPreset[];
}

export type LeechAction = 'Suspend' | 'NotifyOnly';

export interface DerivationCategoryDto {
  key: string;
  label: string;
  exampleBase: string;
  exampleDerived: string;
  explanation: string;
  pairCount: number;
}

export interface DerivationCategoryGroupDto {
  key: string;
  label: string;
  explanation: string;
  pairCount: number;
  categories: DerivationCategoryDto[];
}

/** Per-group marginal coverage for the viewer's own vocabulary. */
export interface DerivationPersonalSummaryDto {
  totalCoveredWords: number;
  groups: DerivationGroupPersonalDto[];
}

export interface DerivationGroupPersonalDto {
  key: string;
  enabled: boolean;
  /** Enabled: words covered thanks to this group; disabled: words enabling it would newly cover. */
  coveredWords: number;
}

/** Per-user marking for one group's preview list. Keys are (wordId << 8) | readingIndex, matching a shown row. */
export interface DerivationPersonalPairsDto {
  /** Forms already redundant under the current selection, whichever group earns them. */
  redundantKeys: number[];
  /** This group's own marginal contribution, matching its count in the personal summary. */
  addedByGroupKeys: number[];
  /** Forms in this group that already count as known, on either side of the arrow. */
  studiedKeys: number[];
}

/** One base→derived mapping in the settings-page preview list. */
export interface DerivationPairDto {
  baseWordId: number;
  baseReadingIndex: number;
  baseText: string;
  baseDefinition: string | null;
  derivedWordId: number;
  derivedReadingIndex: number;
  derivedText: string;
  derivedDefinition: string | null;
  /** The derived form's rank; 0 when unranked. */
  frequencyRank: number;
  categoryLabel: string;
  /** False on one-way pairs: the base covers the derived form but not the reverse. */
  bidirectional: boolean;
}

/** One end of a derivation link shown on the word page. */
export interface WordDerivationDto {
  wordId: number;
  readingIndex: number;
  text: string;
  rubyText: string;
  categoryKey: string;
  categoryLabel: string;
  /** True when the viewer has this category enabled, so the link currently confers knowledge; null when signed out. */
  enabled: boolean | null;
}

export interface DerivationCoverDto {
  wordId: number;
  readingIndex: number;
  text: string;
  categoryKey: string;
  categoryLabel: string;
}

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
  hasStudyDecks: boolean;
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

// The three time-window views of a per-window stats block.
export interface StatWindows<T> {
  last30: T;
  last90: T;
  all: T;
}

export interface AnswerButtonsDto {
  // Each array is indexed [again, hard, good, easy].
  learning: number[];
  young: number[];
  mature: number[];
}

export interface HourlyReviewDto {
  count: number;
  passRate: number | null;
}

// Per-window review-time stats; bucketLabels are window-invariant (top level).
export interface ReviewTimeWindowDto {
  buckets: number[];
  averageSeconds: number | null;
  totalHours: number;
  count: number;
}

export interface ReviewTimeDto extends StatWindows<ReviewTimeWindowDto> {
  bucketLabels: string[];
}

export interface RetentionTodayDto {
  reviews: number;
  passRate: number | null;
  minutes: number;
  newCards: number;
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
  answerButtons: StatWindows<AnswerButtonsDto>;
  hourly: StatWindows<HourlyReviewDto[]>;
  reviewTime: ReviewTimeDto;
  today: RetentionTodayDto;
}

export interface CardStateCountsDto {
  new: number;
  learning: number;
  relearning: number;
  young: number;
  mature: number;
  suspended: number;
  mastered: number;
  blacklisted: number;
  total: number;
}

export interface DifficultyStatsDto {
  buckets: number[];
  medianPct: number | null;
  count: number;
}

export interface StabilityStatsDto {
  buckets: number[];
  bucketLabels: string[];
  medianDays: number | null;
  count: number;
}

export interface RetrievabilityStatsDto {
  buckets: number[];
  averagePct: number | null;
  estimatedKnowledge: number;
  count: number;
  masteredCount: number;
}

export interface CardStatsResponseDto {
  stateCounts: CardStateCountsDto;
  difficulty: DifficultyStatsDto;
  stability: StabilityStatsDto;
  retrievability: RetrievabilityStatsDto;
  leeches: LeechStatsDto;
}

export interface LeechStatsDto {
  threshold: number;
  active: number;
  suspended: number;
  recovered: number;
  top: LeechTopCardDto[];
}

export interface LeechTopCardDto {
  wordId: number;
  readingIndex: number;
  wordText: string;
  lapses: number;
  state: number;
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
  frequencyMediaType?: MediaType;
  frequencyListId?: number;
}

export interface BatchAddStudyDecksRequest {
  deckIds: number[];
  downloadType: number;
  minOccurrences: number;
  deactivateOthers: boolean;
  addToTop: boolean;
}

export interface BatchAddStudyDecksResult {
  added: number[];
  skipped: number[];
  notFound: number[];
  stoppedAtCap: boolean;
  limit: number;
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
  frequencyMediaType?: MediaType;
  frequencyListId?: number;
}

export interface CorpusSnippet {
  html: string;
  text: string;
  deckId: number;
  deckTitle: string;
  parentTitle: string | null;
  mediaType: MediaType;
  difficulty: number;
  releaseYear: number;
}

export interface CorpusMediaBreakdown {
  mediaType: MediaType;
  deckCount: number;
  totalCharacters: number;
  occurrences: number;
  hitsPerMillion: number;
  percentage: number;
}

export interface CorpusTrendPoint {
  year: number;
  occurrences: number;
  totalCharsInYear: number;
  percentage: number;
}

export interface CorpusDifficultyBucket {
  bucketMin: number;
  bucketMax: number;
  deckCount: number;
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
  excludedTerms: string[];
  totalOccurrences: number;
  matchingDecks: number;
  hitsPerMillion: number;
  worksMatched: number;
  worksTotal: number;
  workRangePercentage: number;
  dispersion: number;
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
  totalWorks: number;
}

export interface CorpusFilteredScope {
  hasFilters: boolean;
  decks: number;
  works: number;
  characters: number;
}

export interface CorpusSearchResponse {
  results: CorpusTermResult[];
  corpusStats: CorpusStats;
  filteredScope: CorpusFilteredScope;
}

export interface CorpusCoOccurrence {
  termA: string;
  termB: string;
  sharedDecks: number;
}

export type CardMediaKind = 'image' | 'audio';

export interface CardMediaDto {
  kind: CardMediaKind;
  url: string;
  contentType: string;
  fileSizeBytes: number;
  createdAt: string;
  inherited: boolean;
  sourceReadingIndex: number;
}

export interface CardMediaQuotaDto {
  usedBytes: number;
  maxBytes: number;
}

export interface CardMediaUploadResponse {
  media: CardMediaDto;
  quota: CardMediaQuotaDto;
}

export interface CardMediaEntry {
  wordId: number;
  readingIndex: number;
  image: CardMediaDto | null;
  audio: CardMediaDto | null;
}

export interface CardMediaBatchResponse {
  items: CardMediaEntry[];
}

export interface CardMediaManageFile {
  url: string;
  fileSizeBytes: number;
  createdAt: string;
  contentType: string;
}

export interface CardMediaManageItem {
  wordId: number;
  readingIndex: number;
  wordText: string;
  totalBytes: number;
  image: CardMediaManageFile | null;
  audio: CardMediaManageFile | null;
}

export interface CardMediaManageSummary {
  totalForms: number;
  imageCount: number;
  imageBytes: number;
  audioCount: number;
  audioBytes: number;
  usedBytes: number;
  maxBytes: number;
}

export interface CardMediaManageResponse {
  items: CardMediaManageItem[];
  page: number;
  pageSize: number;
  totalForms: number;
  summary: CardMediaManageSummary;
}

export type CardMediaSort = 'size' | 'date_desc' | 'date_asc';
export type CardMediaKindFilter = 'all' | 'image' | 'audio';

export interface CardMediaDeleteTarget {
  wordId: number;
  readingIndex: number;
  kind: CardMediaKind;
}

export interface CardMediaDeleteBatchResponse {
  deleted: number;
  quota: CardMediaQuotaDto;
}

export type CardBlockType =
  | 'cardStatus'
  | 'headword'
  | 'cardImage'
  | 'exampleSentence'
  | 'confusableReadings'
  | 'frequencyRank'
  | 'etymology'
  | 'definitions'
  | 'customMeaning'
  | 'pitchAccent'
  | 'kanjiBreakdown'
  | 'wordComposition'
  | 'wordUsedIn'
  | 'deckOccurrences'
  | 'divider';

export type HeadwordFurigana = 'hidden' | 'shown' | 'newOnly' | 'afterFlip';

export type CardTextSize = 'small' | 'medium' | 'large';

export interface HeadwordBlockOptions {
  furigana: HeadwordFurigana;
  showAudioButton: boolean;
  size: CardTextSize;
}

export interface ExampleSentenceBlockOptions {
  blur: boolean;
  showSource: boolean;
  showActions: boolean;
  unblurOnFlip: boolean;
  size: CardTextSize;
}

export interface FrequencyRankBlockOptions {
  onlyAfterFlip: boolean;
}

export interface DefinitionsBlockOptions {
  maxDefinitions: number | null;
  size: CardTextSize;
  spoiler: boolean;
}

export interface CustomMeaningBlockOptions {
  size: CardTextSize;
  spoiler: boolean;
}

export interface EtymologyBlockOptions {
  spoiler: boolean;
}

export interface ConfusableReadingsBlockOptions {
  spoiler: boolean;
}

export interface PitchAccentBlockOptions {
  hideHeading: boolean;
  spoiler: boolean;
}

export interface KanjiBreakdownBlockOptions {
  hideHeading: boolean;
  spoiler: boolean;
}

export interface WordCompositionBlockOptions {
  hideHeading: boolean;
  spoiler: boolean;
}

export interface WordUsedInBlockOptions {
  hideHeading: boolean;
  spoiler: boolean;
}

export interface DeckOccurrencesBlockOptions {
  collapsed: boolean;
}

export interface CardImageBlockOptions {
  layout: CardImageLayout;
  blur: boolean;
  showEditButton: boolean;
}

export interface DividerBlockOptions {
  style: 'line' | 'space';
  label?: string;
}

export type CardBlockOptions = Partial<
  HeadwordBlockOptions &
    ExampleSentenceBlockOptions &
    FrequencyRankBlockOptions &
    DefinitionsBlockOptions &
    CustomMeaningBlockOptions &
    EtymologyBlockOptions &
    ConfusableReadingsBlockOptions &
    PitchAccentBlockOptions &
    KanjiBreakdownBlockOptions &
    WordCompositionBlockOptions &
    WordUsedInBlockOptions &
    DeckOccurrencesBlockOptions &
    CardImageBlockOptions &
    DividerBlockOptions
>;

export interface CardLayoutBlock {
  id: string;
  type: CardBlockType;
  options?: CardBlockOptions;
}

export interface CardLayout {
  version: 1;
  front: CardLayoutBlock[];
  back: CardLayoutBlock[];
}

export interface CardLayoutPreset {
  name: string;
  layout: CardLayout;
}

export interface JitenPlusLimitPair {
  free: number;
  plus: number;
}

export interface JitenPlusPricingInfo {
  lifetimeWindowEnd: string;
  lifetimeAvailable: boolean;
  cardMediaStorage: { trialBytes: number; fullBytes: number };
  limits: {
    studyDecks: JitenPlusLimitPair;
    studyDeckWords: JitenPlusLimitPair;
    importWords: JitenPlusLimitPair;
    activeMediaRequests: JitenPlusLimitPair;
    customSentencesPerWord: JitenPlusLimitPair;
  };
}

export interface SiteUpdate {
  id: number;
  title: string;
  bodyMarkdown: string;
  publishedAt: string;
  updatedAt?: string | null;
}

export interface AdminSiteUpdate {
  id: number;
  title: string;
  bodyMarkdown: string;
  notificationTeaser?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  publishedAt?: string | null;
  notifiedAt?: string | null;
}

export interface PollOption {
  id: number;
  text: string;
  sortOrder: number;
  /** Null until results are visible to the caller. */
  voteCount: number | null;
}

export interface Poll {
  id: number;
  question: string;
  descriptionMarkdown?: string | null;
  maxSelections: number;
  publishedAt?: string | null;
  closesAt?: string | null;
  isClosed: boolean;
  myOptionIds: number[];
  resultsVisible: boolean;
  totalVoters: number | null;
  options: PollOption[];
}

export interface AdminPollOption {
  id: number;
  text: string;
  sortOrder: number;
  voteCount: number;
}

export interface AdminPoll {
  id: number;
  question: string;
  descriptionMarkdown?: string | null;
  maxSelections: number;
  createdAt: string;
  updatedAt?: string | null;
  publishedAt?: string | null;
  closesAt?: string | null;
  closedAt?: string | null;
  isClosed: boolean;
  totalVoters: number;
  options: AdminPollOption[];
}

export type JourneyGranularity = 'weekly' | 'monthly';

export interface GrowthPoint {
  date: string;
  knownWords: number;
  knownWordsCombined: number;
  priorKnownWords?: number;
}

export interface JourneyPoint extends GrowthPoint {
  coverage: number;
  combinedCoverage: number;
  uniqueCoverage: number;
  combinedUniqueCoverage: number;
  priorCoverage?: number;
  priorUniqueCoverage?: number;
}

export interface JourneyMilestone {
  threshold: number;
  reachedAt: string;
  unique: boolean;
}

export interface CoverageJourney {
  deckId: number;
  granularity: JourneyGranularity;
  points: JourneyPoint[];
  milestones: JourneyMilestone[];
  startDate: string | null;
  startCoverage: number;
  currentCoverage: number;
  startUniqueCoverage: number;
  currentUniqueCoverage: number;
  hasEnoughHistory: boolean;
  asOf: string | null;
}

export interface KnowledgeGrowth {
  granularity: JourneyGranularity;
  points: GrowthPoint[];
  hasEnoughHistory: boolean;
  recentGain: number;
}

export interface ResolvedWord {
  word: string;
  reading: string;
  wordId: number;
  readingIndex: number;
  forms: string[];
}

export interface ResolveWordsResponse {
  resolved: ResolvedWord[];
}

export interface ImportExampleSentenceItem {
  index: number;
  wordId: number;
  readingIndex: number;
  text: string;
  source?: string;
}

export type ImportExampleSentenceStatus = 'ok' | 'duplicate' | 'limit_reached' | 'no_marker' | 'too_long' | 'invalid';

export interface ImportExampleSentenceResult {
  index: number;
  status: ImportExampleSentenceStatus;
  userExampleSentenceId?: number;
}

export interface ImportExampleSentencesResponse {
  results: ImportExampleSentenceResult[];
  limitPerWord: number;
}

export interface CardMediaBatchEntry {
  kind: 'image' | 'audio';
  url: string;
  contentType: string;
  fileSizeBytes: number;
  createdAt: string;
  inherited: boolean;
  sourceReadingIndex: number;
}

export interface CardMediaBatchItem {
  wordId: number;
  readingIndex: number;
  image: CardMediaBatchEntry | null;
  audio: CardMediaBatchEntry | null;
}

export interface CardMediaBatchResponse {
  items: CardMediaBatchItem[];
}

export type CardMediaImportStatus = 'ok' | 'conflict' | 'invalid' | 'too_large' | 'quota_exceeded' | 'not_tracked' | 'upload_failed';

export interface CardMediaImportResult {
  index: number;
  status: CardMediaImportStatus;
  kind?: 'image' | 'audio';
  storedBytes?: number;
}

export interface CardMediaImportResponse {
  results: CardMediaImportResult[];
  usedBytes: number;
  maxBytes: number;
}
