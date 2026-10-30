import { useToast } from 'primevue/usetoast';
import type { MediaSuggestion } from '~/types';

export const MAX_PROFILE_PINS = 6;

interface PinReplaceRequest {
  title: string;
  pins: MediaSuggestion[];
  resolve: (replacedDeckId: number | null) => void;
}

/** Client-only: the request holds a resolver, so it never goes through SSR state. */
const replaceRequest = shallowRef<PinReplaceRequest | null>(null);

export const usePinReplaceRequest = () => replaceRequest;

/** Pinned ids are main-entry ids; pass a volume and the server pins its series. */
export function useProfilePins() {
  const { $api } = useNuxtApp();
  const toast = useToast();
  const pinnedIds = useState<number[] | null>('profile-pinned-ids', () => null);

  async function load(): Promise<MediaSuggestion[]> {
    const pins = await $api<MediaSuggestion[]>('user/profile/pinned');
    pinnedIds.value = pins.map((p) => p.deckId);
    return pins;
  }

  async function ensureLoaded() {
    if (pinnedIds.value !== null) return;
    try {
      await load();
    } catch {
      pinnedIds.value = null;
    }
  }

  const isPinned = (deckId: number) => pinnedIds.value?.includes(deckId) ?? false;

  function askReplacement(title: string, pins: MediaSuggestion[]): Promise<number | null> {
    return new Promise((resolve) => {
      replaceRequest.value = {
        title,
        pins,
        resolve: (id) => {
          replaceRequest.value = null;
          resolve(id);
        },
      };
    });
  }

  /** Titles dropped from the list are published as unpinned so every copy of them updates. */
  async function replaceAll(deckIds: number[]) {
    const previous = pinnedIds.value ?? [];
    const result = await $api<{ deckIds: number[] }>('user/profile/pinned', { method: 'PUT', body: { deckIds } });
    pinnedIds.value = result.deckIds;
    for (const id of previous) if (!result.deckIds.includes(id)) publishMediaListChange({ deckId: id, isPinned: false });
  }

  /** Saves a new order; ids left out are unpinned. */
  async function reorder(deckIds: number[]): Promise<boolean> {
    try {
      await replaceAll(deckIds);
      return true;
    } catch (err) {
      showErrorToast(toast, apiErrorMessage(err, "Couldn't update your pinned titles"));
      return false;
    }
  }

  /** Resolves true once the title is pinned; at the cap it first asks which pin to give up. */
  async function pin(deckId: number, title: string): Promise<boolean> {
    try {
      const result = await $api<{ deckIds: number[] }>(`user/profile/pinned/${deckId}`, { method: 'POST' });
      pinnedIds.value = result.deckIds;
      return true;
    } catch (err) {
      if (apiStatusCode(err) !== 409) {
        showErrorToast(toast, apiErrorMessage(err, "Couldn't pin this title"));
        return false;
      }
    }

    try {
      const pins = await load();
      const replaced = await askReplacement(title, pins);
      if (replaced == null) return false;
      await replaceAll(pins.map((p) => (p.deckId === replaced ? deckId : p.deckId)));
      return true;
    } catch (err) {
      showErrorToast(toast, apiErrorMessage(err, "Couldn't pin this title"));
      return false;
    }
  }

  async function unpin(deckId: number): Promise<boolean> {
    try {
      await $api(`user/profile/pinned/${deckId}`, { method: 'DELETE' });
      pinnedIds.value = pinnedIds.value?.filter((id) => id !== deckId) ?? null;
      return true;
    } catch (err) {
      showErrorToast(toast, apiErrorMessage(err, "Couldn't unpin this title"));
      return false;
    }
  }

  return { pinnedIds, load, ensureLoaded, isPinned, pin, unpin, reorder };
}
