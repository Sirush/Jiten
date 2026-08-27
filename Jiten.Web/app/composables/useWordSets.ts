import type { WordSetDto, UserWordSetSubscriptionDto, WordSetSubscribeRequest, Word, PaginatedResponse } from '~/types/types';
import type { WordSetStateType } from '~/types';

export function useWordSets() {
  const { $api } = useNuxtApp();

  const wordSets = ref<WordSetDto[]>([]);
  const subscriptions = ref<UserWordSetSubscriptionDto[]>([]);
  const isLoading = ref(false);
  const error = ref<Error | null>(null);

  const fetchWordSets = async () => {
    isLoading.value = true;
    error.value = null;

    try {
      const result = await $api<WordSetDto[]>('word-sets');
      wordSets.value = result || [];
    } catch (e) {
      error.value = e as Error;
      wordSets.value = [];
    } finally {
      isLoading.value = false;
    }
  };

  const fetchWordSet = async (slug: string): Promise<WordSetDto | null> => {
    error.value = null;
    try {
      return await $api<WordSetDto>(`word-sets/${slug}`);
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const fetchWordSetVocabulary = async (slug: string, offset: number = 0, limit: number = 50): Promise<PaginatedResponse<Word[]> | null> => {
    error.value = null;
    try {
      const result = await $api<PaginatedResponse<Word[]>>(`word-sets/${slug}/vocabulary`, {
        query: { offset, limit },
      });
      return result;
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const fetchSubscriptions = async () => {
    isLoading.value = true;
    error.value = null;

    try {
      const result = await $api<UserWordSetSubscriptionDto[]>('word-sets/subscriptions');
      subscriptions.value = result || [];
    } catch (e) {
      error.value = e as Error;
      subscriptions.value = [];
    } finally {
      isLoading.value = false;
    }
  };

  const subscribe = async (setId: number, state: WordSetStateType): Promise<boolean> => {
    try {
      const body: WordSetSubscribeRequest = { state };
      await $api(`word-sets/${setId}/subscribe`, {
        method: 'POST',
        body,
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const unsubscribe = async (setId: number): Promise<boolean> => {
    try {
      await $api(`word-sets/${setId}/subscribe`, {
        method: 'DELETE',
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const isSubscribed = (setId: number): boolean => {
    return subscriptions.value.some((s) => s.setId === setId);
  };

  const getSubscriptionState = (setId: number): WordSetStateType | null => {
    const sub = subscriptions.value.find((s) => s.setId === setId);
    return sub?.state ?? null;
  };

  return {
    wordSets,
    subscriptions,
    isLoading,
    error,
    fetchWordSets,
    fetchWordSet,
    fetchWordSetVocabulary,
    fetchSubscriptions,
    subscribe,
    unsubscribe,
    isSubscribed,
    getSubscriptionState,
  };
}
