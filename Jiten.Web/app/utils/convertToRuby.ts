import type { KnownState } from '~/types';
import type { HeadwordFuriganaMode } from '~/utils/displayProfile';
import { isKnownForFurigana } from '~/utils/wordState';

const RUBY_MARKUP = /([一-鿿０-ｚ々ヵヶ]+)\[([぀-ゟ゠-ヿ]+)]/g;

/** With `revealOnHover`, a hidden reading is still rendered but stays invisible until its word is hovered. */
export function convertToRubyWithFurigana(text: string, displayFurigana: boolean, revealOnHover = false): string {
  if (displayFurigana || revealOnHover) {
    const rt = displayFurigana ? '<rt>' : '<rt class="furigana-peek">';
    return text.replace(RUBY_MARKUP, (_match, kanji, furigana) => {
      return `<ruby lang="ja">${kanji}<rp>(</rp>${rt}${furigana}</rt><rp>)</rp></ruby>`;
    });
  }

  return text.replace(RUBY_MARKUP, (_match, kanji, _furigana) => {
    return `<ruby lang="ja">${kanji}</ruby>`;
  });
}

/** "Unknown words only" hides the reading only when the word's state is on hand and counts as known. */
export function headwordFuriganaVisible(mode: HeadwordFuriganaMode, knownStates: KnownState[] | null | undefined): boolean {
  if (mode === 'hidden') return false;
  if (mode === 'shown' || !knownStates) return true;
  return !isKnownForFurigana(knownStates);
}
