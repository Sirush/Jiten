import type { KnownState } from '~/types';
import { useJitenStore } from '~/stores/jitenStore';
import { useAuthStore } from '~/stores/authStore';
import { wordColourStyle, wordStateColourKey } from '~/utils/wordState';

/** The chosen #rrggbb for a word outside the watch page, or null when colouring is off, the visitor is signed out, or the state has no colour. */
export function useWordStateHex() {
  const store = useJitenStore();
  const auth = useAuthStore();

  return (knownStates: KnownState[] | null | undefined): string | null => {
    if (!store.colourWordsByState || !auth.isAuthenticated || !knownStates) return null;
    return store.resolvedStateColours[wordStateColourKey(knownStates)] ?? null;
  };
}

/** Inline style for a word outside the watch page, adjusted to stay legible on the current theme. */
export function useWordStateColour() {
  const hexOf = useWordStateHex();

  return (knownStates: KnownState[] | null | undefined): string | undefined => {
    const hex = hexOf(knownStates);
    return hex ? wordColourStyle(hex) : undefined;
  };
}
