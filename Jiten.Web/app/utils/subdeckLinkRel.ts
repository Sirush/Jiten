/** Subdeck pages are noindex; nofollow keeps Googlebot's crawl budget on indexable parent decks. */
export function subdeckLinkRel(deck: { parentDeckId?: number | null }): 'nofollow' | undefined {
  return deck.parentDeckId ? 'nofollow' : undefined;
}
