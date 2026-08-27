import type { Deck, DeckStatus } from '~/types';

interface SetStatusResponse {
  deckId: number;
  status: DeckStatus;
  isFavourite: boolean;
  isIgnored: boolean;
  parentDeckId: number | null;
  parentStatus: DeckStatus | null;
  allChildrenCompleted: boolean;
}

export function useDeckPreference(deck: () => Deck, onUpdate: (updated: Deck) => void) {
  const { $api } = useNuxtApp();

  const toggleFavourite = async () => {
    try {
      const d = deck();
      const newFavouriteState = !d.isFavourite;
      await $api(`/user/deck-preferences/${d.deckId}/favourite`, {
        method: 'POST',
        body: { isFavourite: newFavouriteState },
      });
      onUpdate({ ...d, isFavourite: newFavouriteState });
    } catch (error) {
      console.error('Failed to toggle favourite:', error);
    }
  };

  const toggleIgnore = async () => {
    try {
      const d = deck();
      const newIgnoreState = !d.isIgnored;
      await $api(`/user/deck-preferences/${d.deckId}/ignore`, {
        method: 'POST',
        body: { isIgnored: newIgnoreState },
      });
      onUpdate({ ...d, isIgnored: newIgnoreState });
      return newIgnoreState;
    } catch (error) {
      console.error('Failed to toggle ignore:', error);
      return null;
    }
  };

  const cancelIgnore = async () => {
    try {
      const d = deck();
      await $api(`/user/deck-preferences/${d.deckId}/ignore`, {
        method: 'POST',
        body: { isIgnored: false },
      });
      onUpdate({ ...d, isIgnored: false });
    } catch (error) {
      console.error('Failed to cancel ignore:', error);
    }
  };

  const setStatus = async (status: DeckStatus): Promise<SetStatusResponse | null> => {
    try {
      const d = deck();
      const response = await $api<SetStatusResponse>(`/user/deck-preferences/${d.deckId}/status`, {
        method: 'POST',
        body: { status },
      });
      onUpdate({ ...d, status });
      return response;
    } catch (error) {
      console.error('Failed to set status:', error);
      return null;
    }
  };

  return { toggleFavourite, toggleIgnore, cancelIgnore, setStatus };
}
