import { foldKana } from '~/utils/kana';

/** Folds case, full-width forms and katakana to hiragana, so a title search ignores how the query was typed. */
export function normaliseTitleQuery(text: string): string {
  return foldKana(text.normalize('NFKC').toLowerCase()).trim();
}

type SearchableTitle = { originalTitle: string; romajiTitle?: string | null; englishTitle?: string | null; aliases?: string[] | null };

/** Normalised titles per deck object, built on first search so each keystroke only runs `includes`. */
const haystacks = new WeakMap<SearchableTitle, string>();

/** `query` must already be normalised; an empty query matches everything. */
export function titleMatches(deck: SearchableTitle, query: string): boolean {
  if (!query) return true;
  let haystack = haystacks.get(deck);
  if (haystack === undefined) {
    haystack = [deck.originalTitle, deck.romajiTitle, deck.englishTitle, ...(deck.aliases ?? [])]
      .filter((t): t is string => !!t)
      .map(normaliseTitleQuery)
      .join('\n');
    haystacks.set(deck, haystack);
  }
  return haystack.includes(query);
}
