import type { DeckRelationshipType, Genre, LinkType } from '~/types/enums';
import type { DeckMetadataPatch } from '~/types/types';
import { toCanonicalEdge, type PerspectiveRelationship } from '~/utils/relationshipRoles';

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
  hideDialoguePercentage: boolean;
  hideAverageSentenceLength: boolean;
  genres: Genre[];
  tags: DeckMetadataDraftTag[];
  links: DeckMetadataDraftLink[];
  relationships: PerspectiveRelationship[];
}

const genreKey = (genres: Genre[]) => [...genres].sort((a, b) => a - b).join(',');
const tagKey = (tags: DeckMetadataDraftTag[]) =>
  [...tags]
    .sort((a, b) => a.tagId - b.tagId)
    .map((t) => `${t.tagId}:${t.percentage}`)
    .join(',');
const linkKey = (links: DeckMetadataDraftLink[]) =>
  [...links]
    .map((l) => `${l.linkType}|${l.url.trim()}`)
    .sort()
    .join(',');
const edgeKey = (deckId: number, relationships: PerspectiveRelationship[]) =>
  relationships
    .map((r) => toCanonicalEdge(deckId, r))
    .map((e) => `${e.sourceDeckId}>${e.targetDeckId}:${e.relationshipType}`)
    .sort()
    .join(',');

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
  if (draft.hideDialoguePercentage !== original.hideDialoguePercentage) patch.hideDialoguePercentage = draft.hideDialoguePercentage;
  if (draft.hideAverageSentenceLength !== original.hideAverageSentenceLength) patch.hideAverageSentenceLength = draft.hideAverageSentenceLength;

  if (genreKey(draft.genres) !== genreKey(original.genres)) patch.genres = [...draft.genres];
  if (tagKey(draft.tags) !== tagKey(original.tags)) patch.tags = draft.tags.map((t) => ({ tagId: t.tagId, percentage: t.percentage }));
  if (linkKey(draft.links) !== linkKey(original.links)) patch.links = draft.links.map((l) => ({ linkType: l.linkType, url: l.url.trim() }));
  if (edgeKey(deckId, draft.relationships) !== edgeKey(deckId, original.relationships))
    patch.relationships = draft.relationships.map((r) => toCanonicalEdge(deckId, r));

  return patch;
}

export function isDeckMetadataPatchEmpty(patch: DeckMetadataPatch): boolean {
  return Object.keys(patch).length === 0;
}

export function relationshipExists(relationships: PerspectiveRelationship[], targetDeckId: number, relationshipType: DeckRelationshipType): boolean {
  return relationships.some((r) => r.targetDeckId === targetDeckId && r.relationshipType === relationshipType);
}
