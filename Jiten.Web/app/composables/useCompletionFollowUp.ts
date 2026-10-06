import { type Deck, DeckStatus } from '~/types';
import { useConfirm } from 'primevue/useconfirm';
import { useToast } from 'primevue/usetoast';
import { useAuthStore } from '~/stores/authStore';
import { apiStatusCode } from '~/utils/apiErrorMessage';
import { unitWord } from '~/utils/mediaListEntry';
import type { SeriesTitles } from '~/composables/useMediaListSync';

export interface CompletionResponse {
  parentDeckId?: number | null;
  parentStatus?: DeckStatus | null;
  allChildrenCompleted?: boolean;
  parent?: SeriesTitles | null;
}

export interface RatingRequest {
  deckId: number;
  title: string | null;
}

export const useRatingRequest = () => useState<RatingRequest | null>('completion-rating-request', () => null);

export function useCompletionFollowUp() {
  const { $api } = useNuxtApp();
  const confirm = useConfirm();
  const toast = useToast();
  const authStore = useAuthStore();
  const localiseTitle = useLocaliseTitle();
  const smartDeck = useSmartDeck();
  const { isPlus } = useJitenPlus();
  const { postDeckStatus } = useMediaListApi();
  const ratingRequest = useRatingRequest();

  const openRatingDialog = (deckId: number, title: string | null) => {
    ratingRequest.value = { deckId, title };
  };

  async function isUnrated(deckId: number): Promise<boolean> {
    try {
      await $api(`difficulty-votes/rating/${deckId}`);
      return false;
    } catch (e) {
      return apiStatusCode(e) === 404;
    }
  }

  async function offerRating(deckId: number, title: string | null, beforePrompt?: () => void) {
    if (!authStore.isAuthenticated) return;
    if (!(await isUnrated(deckId))) return;
    beforePrompt?.();
    openRatingDialog(deckId, title);
  }

  async function showUnitReport(deck: Deck) {
    if (!isPlus.value) return;
    await smartDeck.fetchStatus();
    if (!smartDeck.exists.value) return;
    const report = await smartDeck.fetchUnitReport(deck.deckId);
    if (!report) return;
    toast.add({ severity: 'info', summary: 'Smart Deck', detail: smartDeck.unitReportSentence(report, localiseTitle(deck)), life: 6000 });
  }

  async function completeSeries(seriesId: number, title: string | null, date: string | null | undefined) {
    const response = await postDeckStatus({ deckId: seriesId, parentDeckId: null }, DeckStatus.Completed, { date });
    if (response) await offerRating(seriesId, title);
  }

  async function followUpCompletion(
    deck: Deck,
    response: CompletionResponse | null,
    date?: string | null,
    { beforePrompt }: { beforePrompt?: () => void } = {}
  ) {
    if (!response) return;
    if (deck.parentDeckId) void showUnitReport(deck);

    const seriesId = deck.parentDeckId;
    if (response.allChildrenCompleted && seriesId) {
      beforePrompt?.();
      confirm.require({
        message: `You've completed all ${unitWord(deck.mediaType).toLowerCase()} in this series. Mark the series as completed too?`,
        header: 'Complete the series?',
        icon: 'pi pi-check-circle',
        acceptLabel: 'Yes, complete it',
        rejectLabel: "No, it's still ongoing",
        rejectProps: { severity: 'secondary' },
        accept: () => completeSeries(seriesId, response.parent ? localiseTitle(response.parent) : null, date),
      });
      return;
    }

    if (!deck.parentDeckId) await offerRating(deck.deckId, localiseTitle(deck), beforePrompt);
  }

  return { followUpCompletion, openRatingDialog };
}
