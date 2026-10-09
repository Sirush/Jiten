import { DeckRelationshipType } from '~/types/enums';
import type { FranchiseEdge } from '~/types/types';

/**
 * A role is phrased from the TARGET deck's perspective: picking "Sequel" means the target you
 * select is THIS deck's sequel. `flip` says the stored edge points from the target back to this deck.
 */
export type RelationshipRoleOption = { label: string; primaryType: DeckRelationshipType; flip: boolean };

export const relationshipRoleOptions: RelationshipRoleOption[] = [
  { label: 'Sequel', primaryType: DeckRelationshipType.Sequel, flip: true },
  { label: 'Prequel', primaryType: DeckRelationshipType.Sequel, flip: false },
  { label: 'Adaptation', primaryType: DeckRelationshipType.Adaptation, flip: false },
  { label: 'Source material', primaryType: DeckRelationshipType.Adaptation, flip: true },
  { label: 'Fandisc', primaryType: DeckRelationshipType.Fandisc, flip: true },
  { label: 'Main release', primaryType: DeckRelationshipType.Fandisc, flip: false },
  { label: 'Spinoff', primaryType: DeckRelationshipType.Spinoff, flip: true },
  { label: 'Parent series', primaryType: DeckRelationshipType.Spinoff, flip: false },
  { label: 'Side story', primaryType: DeckRelationshipType.SideStory, flip: true },
  { label: 'Main story', primaryType: DeckRelationshipType.SideStory, flip: false },
  { label: 'Alternative', primaryType: DeckRelationshipType.Alternative, flip: false },
];

/**
 * Labels for a relationship already in the list, keyed by its type from THIS deck's perspective
 * (the value the API returns). Describes what the target deck is.
 */
export const relationshipTypeLabels: Record<DeckRelationshipType, string> = {
  [DeckRelationshipType.Sequel]: 'Prequel',
  [DeckRelationshipType.Prequel]: 'Sequel',
  [DeckRelationshipType.Fandisc]: 'Main release',
  [DeckRelationshipType.HasFandisc]: 'Fandisc',
  [DeckRelationshipType.Spinoff]: 'Parent series',
  [DeckRelationshipType.HasSpinoff]: 'Spinoff',
  [DeckRelationshipType.SideStory]: 'Main story',
  [DeckRelationshipType.HasSideStory]: 'Side story',
  [DeckRelationshipType.Adaptation]: 'Adaptation',
  [DeckRelationshipType.SourceMaterial]: 'Source material',
  [DeckRelationshipType.Alternative]: 'Alternative',
  [DeckRelationshipType.SameSeries]: 'Same series',
  [DeckRelationshipType.SameSetting]: 'Same setting',
};

/** Pill labels on deck pages and franchise popovers, keyed by the type from THIS deck's perspective; each names the other deck's role. */
export const relatedMediaLabels: Record<DeckRelationshipType, string> = {
  [DeckRelationshipType.Sequel]: 'Prequel',
  [DeckRelationshipType.Fandisc]: 'Source',
  [DeckRelationshipType.Spinoff]: 'Source',
  [DeckRelationshipType.SideStory]: 'Source',
  [DeckRelationshipType.Adaptation]: 'Adaptation',
  [DeckRelationshipType.Alternative]: 'Alternative',
  [DeckRelationshipType.Prequel]: 'Sequel',
  [DeckRelationshipType.HasFandisc]: 'Fandisc',
  [DeckRelationshipType.HasSpinoff]: 'Spinoff',
  [DeckRelationshipType.HasSideStory]: 'Side Story',
  [DeckRelationshipType.SourceMaterial]: 'Source',
  [DeckRelationshipType.SameSeries]: 'Same Series',
  [DeckRelationshipType.SameSetting]: 'Same Setting',
};

export function relatedMediaLabel(type: DeckRelationshipType): string {
  return relatedMediaLabels[type] ?? 'Unknown';
}

/** The pill label for the other end of a stored edge, seen from `deckId`. */
export function relatedMediaLabelFrom(edge: FranchiseEdge, deckId: number): string {
  return relatedMediaLabel(edge.sourceDeckId === deckId ? edge.relationshipType : getInverseRelationshipType(edge.relationshipType));
}

export type LinkTone = 'sequel' | 'side' | 'spin' | 'fan' | 'adapt' | 'alt';

export interface LinkTypeInfo {
  label: string;
  tone: LinkTone;
  fromRole: string;
  toRole: string;
  /** Reads "<later work> verb <original>". */
  verb: string;
}

/** Story links (types 1-6) as the franchise views and builder name them. */
export const relationshipLinkTypes: Partial<Record<DeckRelationshipType, LinkTypeInfo>> = {
  [DeckRelationshipType.Sequel]: { label: 'sequel', tone: 'sequel', fromRole: 'prequel', toRole: 'sequel', verb: 'is a sequel to' },
  [DeckRelationshipType.SideStory]: { label: 'side story', tone: 'side', fromRole: 'main story', toRole: 'side story', verb: 'is a side story of' },
  [DeckRelationshipType.Spinoff]: { label: 'spin-off', tone: 'spin', fromRole: 'original', toRole: 'spin-off', verb: 'is a spin-off of' },
  [DeckRelationshipType.Fandisc]: { label: 'fandisc', tone: 'fan', fromRole: 'original', toRole: 'fandisc', verb: 'is a fandisc of' },
  [DeckRelationshipType.Adaptation]: { label: 'adaptation', tone: 'adapt', fromRole: 'source', toRole: 'adaptation', verb: 'is an adaptation of' },
  [DeckRelationshipType.Alternative]: {
    label: 'alternative',
    tone: 'alt',
    fromRole: 'alternative',
    toRole: 'alternative',
    verb: 'is an alternative version of',
  },
};

export function linkTypeInfo(type: DeckRelationshipType): LinkTypeInfo {
  return relationshipLinkTypes[type] ?? { label: 'link', tone: 'alt', fromRole: '', toRole: '', verb: 'is linked to' };
}

/** Same series / same setting rows predate series membership; the API rejects them in a save but keeps the stored rows. */
export function isLegacyGroupRelationship(type: DeckRelationshipType): boolean {
  return type === DeckRelationshipType.SameSeries || type === DeckRelationshipType.SameSetting;
}

export function getRelationshipRoleLabel(type: DeckRelationshipType): string {
  return relationshipTypeLabels[type] ?? 'Unknown';
}

/** Mirrors DeckRelationship.GetInverse on the backend. */
export function getInverseRelationshipType(type: DeckRelationshipType): DeckRelationshipType {
  switch (type) {
    case DeckRelationshipType.Sequel:
      return DeckRelationshipType.Prequel;
    case DeckRelationshipType.Prequel:
      return DeckRelationshipType.Sequel;
    case DeckRelationshipType.Fandisc:
      return DeckRelationshipType.HasFandisc;
    case DeckRelationshipType.HasFandisc:
      return DeckRelationshipType.Fandisc;
    case DeckRelationshipType.Spinoff:
      return DeckRelationshipType.HasSpinoff;
    case DeckRelationshipType.HasSpinoff:
      return DeckRelationshipType.Spinoff;
    case DeckRelationshipType.SideStory:
      return DeckRelationshipType.HasSideStory;
    case DeckRelationshipType.HasSideStory:
      return DeckRelationshipType.SideStory;
    case DeckRelationshipType.Adaptation:
      return DeckRelationshipType.SourceMaterial;
    case DeckRelationshipType.SourceMaterial:
      return DeckRelationshipType.Adaptation;
    default:
      return type; // Symmetric types (Alternative, SameSeries, SameSetting) are their own inverse
  }
}

export interface PerspectiveRelationship {
  targetDeckId: number;
  relationshipType: DeckRelationshipType;
  isInverse: boolean;
}

/**
 * Converts a relationship expressed from `deckId`'s perspective into the canonical primary edge the
 * API stores. Getting the direction wrong silently flips edges across the whole franchise graph.
 */
export function toCanonicalEdge(deckId: number, rel: PerspectiveRelationship): FranchiseEdge {
  return {
    sourceDeckId: rel.isInverse ? rel.targetDeckId : deckId,
    targetDeckId: rel.isInverse ? deckId : rel.targetDeckId,
    relationshipType: rel.isInverse ? getInverseRelationshipType(rel.relationshipType) : rel.relationshipType,
  };
}

/** The perspective form of a relationship being added under `role` to the deck being edited. */
export function fromRole(targetDeckId: number, role: RelationshipRoleOption): PerspectiveRelationship {
  return {
    targetDeckId,
    relationshipType: role.flip ? getInverseRelationshipType(role.primaryType) : role.primaryType,
    isInverse: role.flip,
  };
}

export interface EdgeFlow {
  /** The earlier or original work. */
  from: number;
  to: number;
  directed: boolean;
}

/** Story order of a stored edge; Adaptation is stored source -> adaptation, the reverse of every other directed type. */
export function getEdgeFlow(edge: FranchiseEdge): EdgeFlow {
  switch (edge.relationshipType) {
    case DeckRelationshipType.Sequel:
    case DeckRelationshipType.Fandisc:
    case DeckRelationshipType.Spinoff:
    case DeckRelationshipType.SideStory:
      return { from: edge.targetDeckId, to: edge.sourceDeckId, directed: true };
    case DeckRelationshipType.Adaptation:
      return { from: edge.sourceDeckId, to: edge.targetDeckId, directed: true };
    default:
      return { from: edge.sourceDeckId, to: edge.targetDeckId, directed: false };
  }
}
