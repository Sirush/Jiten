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

  it('patches score, note and profile visibility, keeping a cleared score', () => {
    const scored = applyMediaListChange(deck, { deckId: 1, score: 8.5, note: 'Reread the ending', isPrivate: true, isPinned: false });
    expect(scored).toMatchObject({ status: DeckStatus.Ongoing, score: 8.5, note: 'Reread the ending', isPrivate: true, isPinned: false });

    expect(applyMediaListChange(scored, { deckId: 1, score: null })).toMatchObject({ score: null, note: 'Reread the ending' });
  });

  it('returns the same deck when nothing changes or another deck changed', () => {
    expect(applyMediaListChange(deck, { deckId: 1, status: DeckStatus.Ongoing })).toBe(deck);
    expect(applyMediaListChange(deck, { deckId: 2, status: DeckStatus.Completed })).toBe(deck);
  });
});
