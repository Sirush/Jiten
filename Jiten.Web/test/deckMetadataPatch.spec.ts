import { describe, expect, it } from 'vitest';
import { DeckRelationshipType, Genre, LinkType } from '../app/types/enums';
import {
  fromRole,
  getInverseRelationshipType,
  relationshipRoleOptions,
  toCanonicalEdge,
} from '../app/utils/relationshipRoles';
import { buildDeckMetadataPatch, type DeckMetadataDraft } from '../app/utils/deckMetadataPatch';

const DECK_ID = 7;

const baseDraft = (): DeckMetadataDraft => ({
  originalTitle: 'Original',
  romajiTitle: 'Romaji',
  englishTitle: 'English',
  description: 'A description.',
  hideDialoguePercentage: false,
  hideAverageSentenceLength: false,
  genres: [Genre.Action, Genre.Drama],
  tags: [{ tagId: 1, percentage: 50 }],
  links: [{ linkType: LinkType.Vndb, url: 'https://vndb.org/v1' }],
  relationships: [{ targetDeckId: 9, relationshipType: DeckRelationshipType.Sequel, isInverse: false }],
});

const role = (label: string) => relationshipRoleOptions.find((r) => r.label === label)!;

describe('toCanonicalEdge', () => {
  it('keeps a direct edge pointing away from this deck', () => {
    const edge = toCanonicalEdge(DECK_ID, { targetDeckId: 9, relationshipType: DeckRelationshipType.Sequel, isInverse: false });
    expect(edge).toEqual({ sourceDeckId: DECK_ID, targetDeckId: 9, relationshipType: DeckRelationshipType.Sequel });
  });

  it('flips an inverse edge back to its stored direction and primary type', () => {
    const edge = toCanonicalEdge(DECK_ID, { targetDeckId: 9, relationshipType: DeckRelationshipType.Prequel, isInverse: true });
    expect(edge).toEqual({ sourceDeckId: 9, targetDeckId: DECK_ID, relationshipType: DeckRelationshipType.Sequel });
  });

  it('keeps the symmetric Alternative type while still flipping the endpoints', () => {
    const edge = toCanonicalEdge(DECK_ID, {
      targetDeckId: 9,
      relationshipType: DeckRelationshipType.Alternative,
      isInverse: true,
    });
    expect(edge).toEqual({ sourceDeckId: 9, targetDeckId: DECK_ID, relationshipType: DeckRelationshipType.Alternative });
  });

  it('round-trips every role into a primary edge touching this deck', () => {
    for (const option of relationshipRoleOptions) {
      const edge = toCanonicalEdge(DECK_ID, fromRole(9, option));
      expect(edge.relationshipType).toBe(option.primaryType);
      expect(edge.relationshipType).toBeLessThan(100);
      expect([edge.sourceDeckId, edge.targetDeckId]).toContain(DECK_ID);
      expect(edge.sourceDeckId).not.toBe(edge.targetDeckId);
      expect(option.flip ? edge.targetDeckId : edge.sourceDeckId).toBe(DECK_ID);
    }
  });

  it('"Sequel" stores the target as the earlier deck', () => {
    // Picking Sequel means the target IS this deck's sequel, so the canonical edge runs target -> this.
    const edge = toCanonicalEdge(DECK_ID, fromRole(9, role('Sequel')));
    expect(edge).toEqual({ sourceDeckId: 9, targetDeckId: DECK_ID, relationshipType: DeckRelationshipType.Sequel });
  });

  it('"Prequel" stores this deck as the earlier one', () => {
    const edge = toCanonicalEdge(DECK_ID, fromRole(9, role('Prequel')));
    expect(edge).toEqual({ sourceDeckId: DECK_ID, targetDeckId: 9, relationshipType: DeckRelationshipType.Sequel });
  });
});

describe('getInverseRelationshipType', () => {
  it('is an involution for every mapped type', () => {
    for (const type of Object.values(DeckRelationshipType).filter((v): v is DeckRelationshipType => typeof v === 'number')) {
      expect(getInverseRelationshipType(getInverseRelationshipType(type))).toBe(type);
    }
  });
});

describe('buildDeckMetadataPatch', () => {
  it('omits everything when nothing changed', () => {
    expect(buildDeckMetadataPatch(DECK_ID, baseDraft(), baseDraft())).toEqual({});
  });

  it('sends only the changed field', () => {
    const draft = baseDraft();
    draft.englishTitle = 'Renamed';
    expect(buildDeckMetadataPatch(DECK_ID, baseDraft(), draft)).toEqual({ englishTitle: 'Renamed' });
  });

  it('ignores collection ordering', () => {
    const draft = baseDraft();
    draft.genres = [Genre.Drama, Genre.Action];
    expect(buildDeckMetadataPatch(DECK_ID, baseDraft(), draft)).toEqual({});
  });

  it('treats a percentage change as a tag change', () => {
    const draft = baseDraft();
    draft.tags = [{ tagId: 1, percentage: 90 }];
    expect(buildDeckMetadataPatch(DECK_ID, baseDraft(), draft)).toEqual({ tags: [{ tagId: 1, percentage: 90 }] });
  });

  it('sends an empty array when a collection is emptied', () => {
    const draft = baseDraft();
    draft.links = [];
    expect(buildDeckMetadataPatch(DECK_ID, baseDraft(), draft)).toEqual({ links: [] });
  });

  it('sends relationships as canonical edges', () => {
    const draft = baseDraft();
    draft.relationships = [
      ...draft.relationships,
      { targetDeckId: 12, relationshipType: DeckRelationshipType.SourceMaterial, isInverse: true },
    ];
    expect(buildDeckMetadataPatch(DECK_ID, baseDraft(), draft)).toEqual({
      relationships: [
        { sourceDeckId: DECK_ID, targetDeckId: 9, relationshipType: DeckRelationshipType.Sequel },
        { sourceDeckId: 12, targetDeckId: DECK_ID, relationshipType: DeckRelationshipType.Adaptation },
      ],
    });
  });

  it('sends a changed description on its own', () => {
    const draft = baseDraft();
    draft.description = 'Rewritten.';
    expect(buildDeckMetadataPatch(DECK_ID, baseDraft(), draft)).toEqual({ description: 'Rewritten.' });
  });

  it('sends an empty string when the description is cleared', () => {
    const draft = baseDraft();
    draft.description = '';
    expect(buildDeckMetadataPatch(DECK_ID, baseDraft(), draft)).toEqual({ description: '' });
  });

  it('trims titles before comparing', () => {
    const draft = baseDraft();
    draft.originalTitle = '  Original  ';
    expect(buildDeckMetadataPatch(DECK_ID, baseDraft(), draft)).toEqual({});
  });
});
