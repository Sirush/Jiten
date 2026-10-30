import { useToast } from 'primevue/usetoast';
import type { DeckDetails, MediaListDeck } from '~/types';
import { apiErrorMessage } from '~/utils/apiErrorMessage';
import { displayDeckId } from '~/utils/displayDeck';
import type { MediaListChange } from '~/composables/useMediaListSync';

export interface DeckDetailsPatch {
  /** Out of 10 with one decimal; null clears it. */
  score?: number | null;
  /** An empty string clears it. */
  note?: string;
  isPrivate?: boolean;
}

type DetailsDeck = { deckId: number; parentDeckId?: number | null };

/** Score, note, privacy and pin live on the title's main entry; every change is published so each copy of the title updates. */
export function useDeckDetails() {
  const { $api } = useNuxtApp();
  const toast = useToast();
  const pins = useProfilePins();

  function publish(deck: DetailsDeck, details: Partial<DeckDetails> & { deckId: number }) {
    const { deckId, ...fields } = details;
    publishMediaListChange({ deckId: deck.deckId, ...fields });
    if (deckId !== deck.deckId) publishMediaListChange({ deckId, ...fields });
  }

  function load(deckId: number) {
    return $api<DeckDetails>(`user/deck-preferences/${deckId}/details`);
  }

  async function save(deck: DetailsDeck, patch: DeckDetailsPatch): Promise<DeckDetails | null> {
    let details: DeckDetails | null = null;
    const saved = await enqueueDeckSideSave(deck.deckId, async () => {
      try {
        details = await $api<DeckDetails>(`user/deck-preferences/${deck.deckId}/details`, {
          method: 'PATCH',
          body: {
            setScore: 'score' in patch,
            score: patch.score == null ? null : Math.round(patch.score * 10),
            note: patch.note ?? null,
            isPrivate: patch.isPrivate ?? null,
          },
        });
        return true;
      } catch (err) {
        showErrorToast(toast, apiErrorMessage(err, "Couldn't save your changes"));
        return false;
      }
    });
    if (!saved || !details) return null;

    publish(deck, details);
    return details;
  }

  async function setPinned(deck: DetailsDeck, title: string, pinned: boolean): Promise<boolean> {
    const target = displayDeckId(deck);
    const done = pinned ? await pins.pin(target, title) : await pins.unpin(target);
    if (done) publish(deck, { deckId: target, isPinned: pinned });
    return done;
  }

  async function togglePinned(deck: DetailsDeck, title: string, pinned: boolean): Promise<boolean> {
    if (!(await setPinned(deck, title, pinned))) return false;
    toast.add({ severity: 'success', summary: pinned ? 'Pinned to your profile' : 'Unpinned from your profile', life: 2500 });
    return true;
  }

  async function toggleHidden(deck: DetailsDeck & Pick<MediaListDeck, 'isPrivate'>): Promise<boolean> {
    const hiding = !deck.isPrivate;
    if (!(await save(deck, { isPrivate: hiding }))) return false;
    toast.add({ severity: 'success', summary: hiding ? 'Hidden from your profile' : 'Shown on your profile', life: 2500 });
    return true;
  }

  return { load, save, setPinned, togglePinned, toggleHidden };
}

/** The details a library row carries; only the owner's rows have them, and no other deck does. */
export function knownDeckDetails(deck: MediaListDeck): DeckDetails | null {
  if (deck.isPrivate == null) return null;
  return { deckId: deck.deckId, score: deck.score ?? null, note: deck.note ?? null, isPrivate: deck.isPrivate, isPinned: !!deck.isPinned };
}

const DETAIL_FIELDS = ['score', 'note', 'isPrivate', 'isPinned'] as const;

function applyDetailsChange(details: DeckDetails, change: MediaListChange): DeckDetails {
  const fields = DETAIL_FIELDS.filter((field) => field in change && change[field] !== details[field]);
  return fields.length ? { ...details, ...Object.fromEntries(fields.map((field) => [field, change[field]])) } : details;
}

/** A title's details for an editor: seeded from `known` or fetched by `load()`, kept current with changes made anywhere; `score` is the draft and `details.score` the saved value. */
export function useDeckDetailsDraft(deck: () => DetailsDeck, known: DeckDetails | null = null) {
  const api = useDeckDetails();
  const details = ref<DeckDetails | null>(known);
  const score = ref<number | null>(known?.score ?? null);
  const loading = ref(false);
  const failed = ref(false);

  async function load() {
    loading.value = true;
    try {
      const loaded = await api.load(deck().deckId);
      details.value = loaded;
      score.value = loaded.score;
      failed.value = false;
    } catch {
      failed.value = true;
    } finally {
      loading.value = false;
    }
  }

  async function save(patch: DeckDetailsPatch) {
    const saved = await api.save(deck(), patch);
    if (saved) details.value = saved;
    return saved;
  }

  onMediaListChange((change) => {
    if (details.value && change.deckId === details.value.deckId) details.value = applyDetailsChange(details.value, change);
  });

  return { details, score, loading, failed, load, save };
}
