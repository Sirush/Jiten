import { DeckRelationshipType } from '~/types/enums';

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
};

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
      return type; // Alternative is symmetric
  }
}

export interface PerspectiveRelationship {
  targetDeckId: number;
  relationshipType: DeckRelationshipType;
  isInverse: boolean;
}

export interface CanonicalEdge {
  sourceDeckId: number;
  targetDeckId: number;
  relationshipType: DeckRelationshipType;
}

/**
 * Converts a relationship expressed from `deckId`'s perspective into the canonical primary edge the
 * API stores. Getting the direction wrong silently flips edges across the whole franchise graph.
 */
export function toCanonicalEdge(deckId: number, rel: PerspectiveRelationship): CanonicalEdge {
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
