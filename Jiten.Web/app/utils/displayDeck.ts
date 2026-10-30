type DeckRef = { deckId: number; parentDeckId?: number | null };

/** Volumes are listed, scored and pinned as their series, the title's main entry. */
export function displayDeckId(deck: DeckRef): number {
  return deck.parentDeckId || deck.deckId;
}

export function isMainEntry(deck: DeckRef): boolean {
  return displayDeckId(deck) === deck.deckId;
}
