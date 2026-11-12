export enum MediaType {
  Anime = 1,
  Drama = 2,
  Movie = 3,
  Novel = 4,
  NonFiction = 5,
  VideoGame = 6,
  VisualNovel = 7,
  WebNovel = 8,
  Manga = 9,
  Audio = 10,
  YouTube = 11,
}

export enum MediaTypeGroup {
  Prose = 0,
  VisualText = 1,
  AudioVisual = 2,
  NonFiction = 3,
  Unknown = 4,
}

export enum DifficultyRankingMoveMode {
  Merge = 0,
  Insert = 1,
  Unrank = 2,
}

export enum LinkType {
  Web = 1,
  Vndb = 2,
  Tmdb = 3,
  Anilist = 4,
  Mal = 5, // Myanimelist
  GoogleBooks = 6,
  Imdb = 7,
  Igdb = 8,
  Syosetsu = 9,
  Bookmeter = 10,
  Amazon = 11,
  YouTube = 12,
  Kakuyomu = 13,
}

export enum Genre {
  Action = 1,
  Adventure = 2,
  Comedy = 3,
  Drama = 4,
  Ecchi = 5,
  Fantasy = 6,
  Horror = 7,
  Mecha = 8,
  Music = 9,
  Mystery = 10,
  Psychological = 11,
  Romance = 12,
  SciFi = 13,
  SliceOfLife = 14,
  Sports = 15,
  Supernatural = 16,
  Thriller = 17,
  AdultOnly = 18,
}

export enum ReadingType {
  Reading = 0,
  KanaReading = 1,
}

export enum DeckDownloadType {
  Full = 1,
  TopGlobalFrequency = 2,
  TopDeckFrequency = 3,
  TopChronological = 4,
  TargetCoverage = 5,
  OccurrenceCount = 6,
}

export enum DeckFormat {
  Anki = 1,
  Csv = 2,
  Txt = 3,
  TxtRepeated = 4,
  Yomitan = 5,
  Learn = 6,
}

export enum DeckOrder {
  Chronological = 1,
  GlobalFrequency = 2,
  DeckFrequency = 3,
  ImportOrder = 4,
  Random = 5,
  SentenceUnlock = 6,
}

export enum StudyDeckType {
  MediaDeck = 0,
  GlobalDynamic = 1,
  StaticWordList = 2,
  Smart = 3,
  MediaGroup = 4,
}

export enum SortOrder {
  Ascending = 0,
  Descending = 1,
}

export enum TitleLanguage {
  Original = 0,
  Romaji = 1,
  English = 2,
}

export enum DisplayStyle {
  Card = 0,
  Compact = 1,
  Table = 2,
}

export enum KnownState {
  New = 0,
  Young = 1,
  Mature = 2,
  Blacklisted = 3,
  Due = 4,
  Mastered = 5,
  Redundant = 6,
  Suspended = 7,
}

export enum DifficultyDisplayStyle {
  Name = 0,
  NameAndValue = 1,
  Value = 2,
}

export enum DifficultyValueDisplayStyle {
  ZeroToFive = 1,
  Percentage = 2,
}

export enum ThemeMode {
  Light = 'light',
  Dark = 'dark',
  Auto = 'auto',
}

export enum DeckStatus {
  None = 0,
  Planning = 1,
  Ongoing = 2,
  Completed = 3,
  Dropped = 4,
  Paused = 5,
}

export enum MediaListEntryState {
  InProgress = 0,
  Completed = 1,
  Dropped = 2,
}

export enum FsrsState {
  New = 0,
  Learning = 1,
  Review = 2,
  Relearning = 3,
  Blacklisted = 4,
  Mastered = 5,
  Suspended = 6,
}

export enum CardArchiveReason {
  Unknown = 0,
  KanaRedundancy = 1,
  FormPrune = 2,
  RedundancyResolve = 3,
  Forget = 4,
  BulkForget = 5,
  MassAction = 6,
  WordReplacementMerge = 7,
  ImportUndo = 8,
}

export enum CardSource {
  Unknown = 0,
  Review = 1,
  ManualSingle = 2,
  DeckLearn = 3,
  FrequencyImport = 4,
  AnkiImport = 5,
  JpdbImport = 6,
  WaniKaniImport = 7,
  WordListImport = 8,
  BackupRestore = 9,
  CompositionInference = 10,
  PlacementTest = 11,
  ImportExtrapolation = 12,
  KanaRedundancy = 13,
  MassAction = 14,
  WordSet = 15,
  ReadingCalibration = 16,
}

export enum CardBatchKind {
  Other = 0,
  JpdbKnownWords = 1,
  JpdbReviewHistory = 2,
  WordList = 3,
  WordIds = 4,
  AnkiFile = 5,
  AnkiConnect = 6,
  CompoundsOfKnown = 7,
  PartsOfKnown = 8,
  FrequencyRange = 9,
  MediaDeck = 10,
  StudyDeck = 11,
  MediaList = 12,
  BackupRestore = 13,
  WordCheck = 14,
  ReadingCheck = 15,
}

export enum FsrsRating {
  Again = 1,
  Hard = 2,
  Good = 3,
  Easy = 4,
}

export enum DeckRelationshipType {
  Sequel = 1,
  Fandisc = 2,
  Spinoff = 3,
  SideStory = 4,
  Adaptation = 5,
  Alternative = 6,
  SameSeries = 7,
  SameSetting = 8,

  // Inverse relationships
  Prequel = 101,
  HasFandisc = 102,
  HasSpinoff = 103,
  HasSideStory = 104,
  SourceMaterial = 105,
}

export enum WordSetStateType {
  Blacklisted = 1,
  Mastered = 2,
}

export enum RequestStatus {
  Open = 1,
  InProgress = 2,
  Completed = 3,
  Rejected = 4,
}

export enum RequestKind {
  New = 1,
  Update = 2,
}

export enum RequestMetadataStatus {
  None = 0,
  Pending = 1,
  Fetched = 2,
  Failed = 3,
  Unsupported = 4,
}

export enum NotificationType {
  RequestStatusChanged = 1,
  RequestCompleted = 2,
  RequestFileUploaded = 3,
  RequestAdminComment = 4,
  General = 10,
  SiteUpdate = 11,
  CommunityListMilestone = 12,
  MetadataEditApproved = 30,
  MetadataEditRejected = 31,
  MetadataEditReverted = 32,
}

export enum NotificationGroup {
  FromJiten = 0,
  MediaRequests = 1,
  MetadataEdits = 2,
  CommunityLists = 3,
}

export enum RequestAction {
  RequestCreated = 1,
  RequestDeleted = 2,
  Upvoted = 10,
  UpvoteRemoved = 11,
  Subscribed = 12,
  Unsubscribed = 13,
  CommentAdded = 20,
  CommentEdited = 21,
  FileUploaded = 30,
  FileDeletedByAdmin = 31,
  StatusChangedToInProgress = 40,
  StatusChangedToCompleted = 41,
  StatusChangedToRejected = 42,
  StatusChangedToOpen = 43,
  RequestEditedByAdmin = 50,
  RequestEditedByRequester = 51,
  ContributionValidated = 60,
  ContributionRevoked = 61,
}

const proseTypes = new Set([MediaType.Novel, MediaType.WebNovel, MediaType.VisualNovel]);
const visualTextTypes = new Set([MediaType.Manga, MediaType.VideoGame]);
const audioVisualTypes = new Set([MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio, MediaType.YouTube]);
const nonFictionTypes = new Set([MediaType.NonFiction]);
const allTextTypes = new Set([...proseTypes, ...visualTextTypes, ...nonFictionTypes]);

export function areComparable(a: MediaType, b: MediaType): boolean {
  if ((allTextTypes.has(a) && audioVisualTypes.has(b)) || (audioVisualTypes.has(a) && allTextTypes.has(b))) return false;
  return true;
}

export enum ComparisonOutcome {
  MuchEasier = -2,
  Easier = -1,
  Same = 0,
  Harder = 1,
  MuchHarder = 2,
}

export enum CommunityListStatus {
  Draft = 0,
  Public = 1,
  Unlisted = 2,
}

export enum ForumDiscussionStatus {
  Ready = 0,
  Unavailable = 2,
  /** No topic yet; a signed-in reader creates it by starting the discussion. */
  NotStarted = 3,
}

export enum WordCheckAnswer {
  Know = 0,
  NotSure = 1,
  No = 2,
}

export enum ReadingCheckAnswer {
  Knew = 0,
  Partly = 1,
  DidntKnow = 2,
}
