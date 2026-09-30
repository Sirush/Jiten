import type { KnownState } from '~/types';
import { useJitenStore } from '~/stores/jitenStore';
import { useAuthStore } from '~/stores/authStore';
import { convertToRubyWithFurigana, headwordFuriganaVisible } from '~/utils/convertToRuby';
import { sanitiseHtml } from '~/utils/sanitiseHtml';

/**
 * Pass the word's states where the caller has them; without them "unknown words only" shows the furigana.
 * A forced value is never revealed on hover, since callers force furigana off to hide an answer.
 */
export function useConvertToRuby() {
  const store = useJitenStore();
  const auth = useAuthStore();

  return (text: string, forceDisplayFurigana?: boolean, knownStates?: KnownState[] | null): string => {
    const show = forceDisplayFurigana ?? headwordFuriganaVisible(store.headwordFurigana, auth.isAuthenticated ? knownStates : undefined);
    const revealOnHover = forceDisplayFurigana === undefined && store.furiganaOnHover;
    return sanitiseHtml(convertToRubyWithFurigana(text, show, revealOnHover));
  };
}
