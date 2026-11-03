import type { DeckRelationshipType, LinkType, MediaType } from './enums';

export enum MetadataField {
  OriginalTitle = 1,
  RomajiTitle = 2,
  EnglishTitle = 4,
  Aliases = 8,
  ReleaseDate = 16,
  Description = 32,
  MediaType = 64,
  Genres = 128,
  Tags = 256,
  Links = 512,
  Relationships = 1024,
  CharacterNames = 2048,
}

export enum MetadataRiskTier {
  Low = 0,
  Medium = 1,
  High = 2,
}

export enum MetadataEditStatus {
  Pending = 1,
  Applied = 2,
  Approved = 3,
  Rejected = 4,
  Reverted = 5,
  Superseded = 6,
  Withdrawn = 7,
}

export enum MetadataEditVerdict {
  Confirm = 1,
  Doubt = 2,
}

export enum MetadataEditDoubtReason {
  SourceDoesNotMatch = 1,
  WrongValue = 2,
  NoSource = 3,
  Other = 4,
}

export enum MetadataEditActor {
  User = 1,
  Provider = 2,
  System = 3,
}

export type MetadataViewerRole = 'user' | 'reviewer' | 'editor';
export type MetadataPublicStatus = 'live' | 'pending' | 'rejected' | 'reverted' | 'superseded' | 'withdrawn';
export type MetadataContributorLevel = 'new' | 'established' | 'trusted' | 'editor';

export interface MetadataTagValue {
  tagId: number;
  name: string;
  percentage: number;
}

export interface MetadataLinkValue {
  linkType: LinkType | number;
  url: string;
}

export interface MetadataRelationshipValue {
  sourceDeckId: number;
  targetDeckId: number;
  relationshipType: DeckRelationshipType | number;
  otherDeckId: number;
  otherDeckTitle: string;
  /** Sent with recorded edits; null once the other deck is gone. */
  otherDeckMediaType?: MediaType | null;
  otherDeckReleaseYear?: number | null;
}

export interface MetadataFieldChange {
  field: MetadataField;
  before: unknown;
  after: unknown;
}

export interface EditableDeckViewer {
  role: MetadataViewerRole;
  hasDisplayName: boolean;
  emailConfirmed: boolean;
  banned: boolean;
}

export interface EditablePendingEdit {
  id: number;
  fields: number;
  isYours: boolean;
  createdAt: string;
}

export interface EditableDeck {
  deckId: number;
  coverName: string;
  mediaType: MediaType;
  characterCount: number;
  releaseYear: number | null;
  originalTitle: string;
  romajiTitle: string | null;
  englishTitle: string | null;
  aliases: string[];
  releaseDate: string | null;
  description: string | null;
  genres: number[];
  tags: MetadataTagValue[];
  links: MetadataLinkValue[];
  relationships: (MetadataRelationshipValue & { otherDeckMediaType: MediaType })[];
  characterNames: string[];
  historyCount: number;
  pending: EditablePendingEdit[];
  viewer: EditableDeckViewer;
}

export type CharacterNameSurfaceStatus = 'ok' | 'flagged' | 'notInText' | 'risky' | 'duplicate';

export interface CharacterNameSurface {
  surface: string;
  /** Only sent to reviewers and editors. */
  occurrences: number | null;
  found: boolean;
  status: CharacterNameSurfaceStatus;
  detail: string | null;
  /** Short excerpts of the deck's text around the name, on queue cards only. */
  examples: string[] | null;
}

export interface CharacterNameCheck {
  input: string;
  error: string | null;
  surfaces: CharacterNameSurface[];
}

export interface SubmitMetadataEdit {
  originalTitle?: string;
  romajiTitle?: string;
  englishTitle?: string;
  description?: string;
  releaseDate?: string | null;
  mediaType?: number;
  aliases?: string[];
  genres?: number[];
  tags?: { tagId: number; percentage: number }[];
  links?: { linkType: number; url: string }[];
  relationships?: { sourceDeckId: number; targetDeckId: number; relationshipType: number }[];
  addCharacterNames?: string[];
  removeCharacterNames?: string[];
  sources: string[];
  note?: string;
  base?: SubmitMetadataEditBase;
}

/** What the form loaded for each field it sends, so the API can replay list changes and catch scalars that moved meanwhile. */
export type SubmitMetadataEditBase = Omit<SubmitMetadataEdit, 'sources' | 'note' | 'base' | 'addCharacterNames' | 'removeCharacterNames'>;

export interface SubmitConflictField {
  field: MetadataField;
  current: unknown;
}

export interface SubmitMetadataEditResult {
  submissionId: string;
  appliedFields: number[];
  pendingFields: number[];
  editIds: number[];
}

export interface MetadataQueueCheck {
  displayName: string;
  verdict: MetadataEditVerdict;
  reason: MetadataEditDoubtReason | null;
  note: string | null;
  level: string;
  accepted: number;
  createdAt: string;
  matched: number;
  missed: number;
  /** Doubts only: the fields that look wrong; 0 means the whole edit. */
  fields: number;
}

export interface MetadataQueueConflict {
  field: MetadataField;
  editBefore: unknown;
  current: unknown;
  editAfter: unknown;
}

export interface MetadataFranchiseRef {
  franchiseId: number;
  originalTitle: string;
  romajiTitle: string | null;
  englishTitle: string | null;
  deckCount: number;
}

/** into is null when the deck has no franchise yet and joins the others; otherwise they merge into it. */
export interface MetadataFranchiseEffect {
  joining: MetadataFranchiseRef[];
  into: MetadataFranchiseRef | null;
}

export interface MetadataQueueItem {
  id: number;
  submissionId: string;
  deckId: number;
  deck: {
    originalTitle: string;
    romajiTitle: string | null;
    englishTitle: string | null;
    coverName: string;
    mediaType: MediaType;
    releaseYear: number | null;
  };
  createdAt: string;
  author: { displayName: string; isYou: boolean };
  fields: number;
  changes: MetadataFieldChange[];
  sources: string[];
  note: string | null;
  confirmCount: number;
  myCheck: { verdict: MetadataEditVerdict; reason: MetadataEditDoubtReason | null; note: string | null; fields: number } | null;
  canCheck: boolean;
  checkBlockedReason: string | null;
  characterNameChecks: CharacterNameSurface[] | null;
  franchiseEffect: MetadataFranchiseEffect | null;
  tier: MetadataRiskTier | null;
  authorRecord: { level: MetadataContributorLevel; accepted: number; reverted: number } | null;
  checks: MetadataQueueCheck[] | null;
  doubtCount: number | null;
  conflicts: MetadataQueueConflict[] | null;
  canApprove: boolean;
  status: MetadataEditStatus;
  canMarkReviewed: boolean;
  canRevert: boolean;
}

export interface MetadataQueueResponse {
  viewerRole: MetadataViewerRole;
  total: number;
  /** The other tab's total (pending vs unreviewed); null for contributors, who only see pending. */
  otherTotal: number | null;
  items: MetadataQueueItem[];
}

export interface MetadataNamePreview {
  linesChecked: number;
  linesChanged: number;
  examples: { line: string; before: string; after: string }[];
}

export interface MetadataRevertResult {
  revertedFields: number[];
  skippedFields: number[];
}

export interface MetadataHistoryItem {
  id: number;
  createdAt: string;
  actor: MetadataEditActor;
  author: { displayName: string } | null;
  provider: LinkType | null;
  publicStatus: MetadataPublicStatus;
  status: MetadataEditStatus | null;
  changes: MetadataFieldChange[];
  sources: string[];
  confirmCount: number;
  reviewNote: string | null;
  revertedFields: number[] | null;
  skippedFields: number[] | null;
  reviewer: { displayName: string } | null;
  canRevert: boolean;
  isYours: boolean;
  contentHidden: boolean;
}

export interface MetadataDeckHistory {
  viewerRole: MetadataViewerRole;
  total: number;
  items: MetadataHistoryItem[];
  origin: { createdAt: string; providers: LinkType[] };
}

export interface MetadataRecentItem {
  id: number;
  createdAt: string;
  author: string | null;
  publicStatus: MetadataPublicStatus;
  status: MetadataEditStatus | null;
  fields: number;
  confirmCount: number;
  deck: { deckId: number; originalTitle: string; romajiTitle: string | null; englishTitle: string | null } | null;
  changes: MetadataFieldChange[];
  isYours: boolean;
  contentHidden: boolean;
}

export interface MetadataRecentResponse {
  viewerRole: MetadataViewerRole;
  total: number;
  items: MetadataRecentItem[];
}

export interface MetadataContributions {
  accepted: number;
  decksImproved: number;
  pending: number | null;
  checked: number;
}

export interface MetadataEditSettings {
  establishedMinAccountDays: number;
  establishedCountedEdits: number;
  establishedNoRevertDays: number;
  trustedCountedEdits: number;
  trustedNoRevertDays: number;
  establishedAutoApplyMaxTier: number;
  trustedAutoApplyMaxTier: number;
}

export type MetadataContributorRole = 'none' | 'reviewer' | 'editor';

export interface MetadataContributor {
  userId: string;
  displayName: string | null;
  email: string;
  roles: string[];
  level: string;
  levelReason: string;
  counted: number;
  accepted: number;
  reverted: number;
  checks: number;
  adminTrusted: boolean;
  blocked: boolean;
  banned: boolean;
  checksConfirmedApproved: number;
  checksConfirmedRejected: number;
  checksDoubtedApproved: number;
  checksDoubtedRejected: number;
}

export interface MetadataEditMetrics {
  pending: number;
  oldestPendingHours: number | null;
  liveUnreviewed: number;
  medianHoursToDecision30d: number | null;
  decided30d: number;
  approved30d: number;
  rejected30d: number;
  reverted30d: number;
  withdrawn30d: number;
  submissions30d: number;
  autoApplied30d: number;
  weeks: { weekStart: string; submissions: number; decisions: number; autoApplied: number }[];
}
