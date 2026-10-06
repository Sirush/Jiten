import { describe, expect, it } from 'vitest';
import { DeckStatus, type Deck, type MediaListEntrySummary } from '~/types';
import { applyMediaListChange } from '~/composables/useMediaListSync';

const deck = { deckId: 1, status: DeckStatus.Ongoing, isFavourite: false, listEntry: null } as unknown as Deck;
const entry = { entryId: 5 } as MediaListEntrySummary;

describe('applyMediaListChange', () => {
  it('patches only the fields the change carries', () => {
    const favourited = applyMediaListChange(deck, { deckId: 1, isFavourite: true });
    expect(favourited).toMatchObject({ status: DeckStatus.Ongoing, isFavourite: true, listEntry: null });

    const completed = applyMediaListChange(deck, { deckId: 1, status: DeckStatus.Completed, listEntry: entry });
    expect(completed).toMatchObject({ status: DeckStatus.Completed, isFavourite: false, listEntry: entry });
  });

  it('returns the same deck when nothing changes or another deck changed', () => {
    expect(applyMediaListChange(deck, { deckId: 1, status: DeckStatus.Ongoing })).toBe(deck);
    expect(applyMediaListChange(deck, { deckId: 2, status: DeckStatus.Completed })).toBe(deck);
  });
});
