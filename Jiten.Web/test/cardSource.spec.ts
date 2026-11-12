import { describe, expect, it } from 'vitest';
import { CardBatchKind, CardSource, DeckStatus } from '~/types/enums';
import type { CardBatchDeck, CardBatchDetails } from '~/types/types';
import { batchDetail, cardSourceDisplay } from '~/utils/cardSource';

describe('cardSourceDisplay', () => {
  it('labels every source', () => {
    const sources = Object.values(CardSource).filter((v): v is CardSource => typeof v === 'number');
    for (const source of sources) {
      const display = cardSourceDisplay(source);
      expect(display.label.length).toBeGreaterThan(0);
      expect(display.icon).toMatch(/^pi pi-/);
    }
  });

  it('falls back to Other for a source the client does not know yet', () => {
    expect(cardSourceDisplay(99 as CardSource).label).toBe('Other');
  });
});

describe('batchDetail', () => {
  const englishTitle = (deck: CardBatchDeck) => deck.englishTitle ?? deck.originalTitle;
  const detail = (details: CardBatchDetails, deck: CardBatchDeck | null = null) => batchDetail({ details, deck }, englishTitle);

  it('localises a media deck title', () => {
    const deck = { deckId: 7, originalTitle: '魔女の旅々', romajiTitle: 'Majo no Tabitabi', englishTitle: 'Wandering Witch' };
    expect(detail({ kind: CardBatchKind.MediaDeck, deckId: 7 }, deck)).toBe('Wandering Witch');
  });

  it('shows nothing for a deck that no longer exists', () => {
    expect(detail({ kind: CardBatchKind.MediaDeck, deckId: 7 })).toBeNull();
  });

  it('formats ranges, units and media lists', () => {
    expect(detail({ kind: CardBatchKind.FrequencyRange, minRank: 1, maxRank: 5000 })).toBe(`Ranks 1 to ${(5000).toLocaleString()}`);
    expect(detail({ kind: CardBatchKind.ReadingCheck, units: 1 })).toBe('1 volume or work');
    expect(detail({ kind: CardBatchKind.ReadingCheck, units: 4 })).toBe('4 volumes and works');
    expect(detail({ kind: CardBatchKind.MediaList, name: 'reader', listStatus: DeckStatus.Completed })).toBe("reader's media list (completed)");
  });

  it('shows the name the user gave', () => {
    expect(detail({ kind: CardBatchKind.AnkiFile, name: 'core.apkg' })).toBe('core.apkg');
    expect(detail({ kind: CardBatchKind.AnkiConnect })).toBe('AnkiConnect');
  });

  it('adds nothing for a kind the source label already covers', () => {
    expect(detail({ kind: CardBatchKind.WordList })).toBeNull();
    expect(detail({ kind: 99 as CardBatchKind })).toBeNull();
  });
});
