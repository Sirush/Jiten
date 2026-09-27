import type { KnownState } from '~/types';
import { useJitenStore } from '~/stores/jitenStore';
import { useAuthStore } from '~/stores/authStore';
import { wordStateColourKey } from '~/utils/wordState';

/** Inline colour for a word outside the watch page, or undefined when colouring is off, the visitor is signed out, or the state has no colour. */
export function useWordStateColour() {
  const store = useJitenStore();
  const auth = useAuthStore();

  return (knownStates: KnownState[] | null | undefined): { color: string } | undefined => {
    if (!store.colourWordsByState || !auth.isAuthenticated || !knownStates) return undefined;
    const colour = store.resolvedStateColours[wordStateColourKey(knownStates)];
    return colour ? { color: colour } : undefined;
  };
}
