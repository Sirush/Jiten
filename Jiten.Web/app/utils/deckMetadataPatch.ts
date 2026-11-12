import type { DeckRelationshipType, Genre, LinkType, MediaType } from '~/types/enums';
import type { DeckMetadataPatch } from '~/types/types';
import { isLegacyGroupRelationship, toCanonicalEdge, type PerspectiveRelationship } from '~/utils/relationshipRoles';

export interface DeckMetadataDraftTag {
  tagId: number;
  percentage: number;
  /** Display only; the patch carries ids. */
  name?: string;
}

export interface DeckMetadataDraftLink {
  linkType: LinkType;
  url: string;
}

export interface DeckMetadataDraft {
  originalTitle: string;
  romajiTitle: string;
  englishTitle: string;
  description: string;
  mediaType: MediaType;
  hideDialoguePercentage: boolean;
  hideAverageSentenceLength: boolean;
  genres: Genre[];
  tags: DeckMetadataDraftTag[];
  links: DeckMetadataDraftLink[];
  relationships: PerspectiveRelationship[];
}

type EdgeLike = { sourceDeckId: number; targetDeckId: number; relationshipType: number };

export const edgeId = (e: EdgeLike) => `${e.sourceDeckId}>${e.targetDeckId}:${e.relationshipType}`;
export const genreKey = (genres: number[]) => [...genres].sort((a, b) => a - b).join(',');
export const tagKey = (tags: { tagId: number; percentage: number }[]) =>
  [...tags]
    .sort((a, b) => a.tagId - b.tagId)
    .map((t) => `${t.tagId}:${t.percentage}`)
    .join(',');
export const linkKey = (links: { linkType: number; url: string }[]) =>
  [...links]
    .map((l) => `${l.linkType}|${l.url.trim()}`)
    .sort()
    .join(',');
export const savableEdges = <T extends EdgeLike>(edges: T[]) => edges.filter((e) => !isLegacyGroupRelationship(e.relationshipType as DeckRelationshipType));
export const edgeKey = (edges: EdgeLike[]) => savableEdges(edges).map(edgeId).sort().join(',');
const canonicalEdges = (deckId: number, relationships: PerspectiveRelationship[]) => relationships.map((r) => toCanonicalEdge(deckId, r));

/**
 * Builds the narrowest patch that expresses the edit. Unchanged fields are omitted so a title-only
 * edit cannot clobber another admin's concurrent tag change.
 */
export function buildDeckMetadataPatch(deckId: number, original: DeckMetadataDraft, draft: DeckMetadataDraft): DeckMetadataPatch {
  const patch: DeckMetadataPatch = {};

  if (draft.originalTitle.trim() !== original.originalTitle.trim()) patch.originalTitle = draft.originalTitle.trim();
  if (draft.romajiTitle.trim() !== original.romajiTitle.trim()) patch.romajiTitle = draft.romajiTitle.trim();
  if (draft.englishTitle.trim() !== original.englishTitle.trim()) patch.englishTitle = draft.englishTitle.trim();
  if (draft.description.trim() !== original.description.trim()) patch.description = draft.description.trim();
  if (draft.mediaType !== original.mediaType) patch.mediaType = draft.mediaType;
  if (draft.hideDialoguePercentage !== original.hideDialoguePercentage) patch.hideDialoguePercentage = draft.hideDialoguePercentage;
  if (draft.hideAverageSentenceLength !== original.hideAverageSentenceLength) patch.hideAverageSentenceLength = draft.hideAverageSentenceLength;

  if (genreKey(draft.genres) !== genreKey(original.genres)) patch.genres = [...draft.genres];
  if (tagKey(draft.tags) !== tagKey(original.tags)) patch.tags = draft.tags.map((t) => ({ tagId: t.tagId, percentage: t.percentage }));
  if (linkKey(draft.links) !== linkKey(original.links)) patch.links = draft.links.map((l) => ({ linkType: l.linkType, url: l.url.trim() }));
  const draftEdges = canonicalEdges(deckId, draft.relationships);
  if (edgeKey(draftEdges) !== edgeKey(canonicalEdges(deckId, original.relationships))) patch.relationships = savableEdges(draftEdges);

  return patch;
}

export function isDeckMetadataPatchEmpty(patch: DeckMetadataPatch): boolean {
  return Object.keys(patch).length === 0;
}

export function relationshipExists(relationships: PerspectiveRelationship[], targetDeckId: number, relationshipType: DeckRelationshipType): boolean {
  return relationships.some((r) => r.targetDeckId === targetDeckId && r.relationshipType === relationshipType);
}
