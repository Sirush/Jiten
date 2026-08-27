import type {
  ComparisonSuggestionDto,
  CompletedDecksResponse,
  DifficultyVoteDto,
  DifficultyRatingDto,
  VotingStatsDto,
  DeckSummaryDto,
  BlacklistedDeckDto,
} from '~/types/types';
import { type ComparisonOutcome } from '~/types';

export function useDifficultyVotes() {
  const { $api } = useNuxtApp();
  const error = ref<Error | null>(null);

  const fetchSuggestions = async (deckId?: number): Promise<ComparisonSuggestionDto[]> => {
    error.value = null;
    try {
      return (
        (await $api<ComparisonSuggestionDto[]>('difficulty-votes/suggestions', {
          query: deckId ? { deckId } : undefined,
        })) ?? []
      );
    } catch (e) {
      error.value = e as Error;
      return [];
    }
  };

  const fetchCompletedDecks = async (): Promise<CompletedDecksResponse> => {
    error.value = null;
    try {
      return (await $api<CompletedDecksResponse>('difficulty-votes/completed-decks')) ?? { decks: [], votedPairs: [] };
    } catch (e) {
      error.value = e as Error;
      return { decks: [], votedPairs: [] };
    }
  };

  const fetchUnratedDecks = async (): Promise<DeckSummaryDto[]> => {
    error.value = null;
    try {
      return (await $api<DeckSummaryDto[]>('difficulty-votes/unrated-decks')) ?? [];
    } catch (e) {
      error.value = e as Error;
      return [];
    }
  };

  const submitVote = async (deckAId: number, deckBId: number, outcome: ComparisonOutcome): Promise<boolean> => {
    error.value = null;
    try {
      await $api('difficulty-votes', {
        method: 'POST',
        body: { deckAId, deckBId, outcome },
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const skipPair = async (deckAId: number, deckBId: number, permanent: boolean): Promise<boolean> => {
    error.value = null;
    try {
      await $api('difficulty-votes/skip', {
        method: 'POST',
        body: { deckAId, deckBId, permanent },
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const fetchStats = async (): Promise<VotingStatsDto | null> => {
    error.value = null;
    try {
      return await $api<VotingStatsDto>('difficulty-votes/stats');
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const fetchMyVotes = async (
    params: {
      type?: string;
      offset?: number;
      limit?: number;
    } = {}
  ): Promise<{ data: DifficultyVoteDto[]; totalItems: number } | null> => {
    error.value = null;
    try {
      return await $api<{ data: DifficultyVoteDto[]; totalItems: number }>('difficulty-votes/mine', {
        query: {
          type: params.type ?? 'comparisons',
          offset: params.offset ?? 0,
          limit: params.limit ?? 20,
        },
      });
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const deleteSkip = async (id: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`difficulty-votes/skip/${id}`, { method: 'DELETE' });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const deleteVote = async (id: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`difficulty-votes/${id}`, { method: 'DELETE' });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const fetchRating = async (deckId: number): Promise<number | null> => {
    error.value = null;
    try {
      const result = await $api<{ rating: number }>(`difficulty-votes/rating/${deckId}`);
      return result?.rating ?? null;
    } catch {
      return null;
    }
  };

  const submitRating = async (deckId: number, rating: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api('difficulty-votes/rating', {
        method: 'POST',
        body: { deckId, rating },
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const deleteRating = async (deckId: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`difficulty-votes/rating/${deckId}`, { method: 'DELETE' });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const fetchMySkipped = async (
    params: {
      offset?: number;
      limit?: number;
    } = {}
  ): Promise<{ data: DifficultyVoteDto[]; totalItems: number } | null> => {
    return fetchMyVotes({ type: 'skipped', ...params });
  };

  const blockDeck = async (deckId: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`difficulty-votes/blacklist/${deckId}`, { method: 'POST' });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const unblockDeck = async (deckId: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`difficulty-votes/blacklist/${deckId}`, { method: 'DELETE' });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const fetchBlockedDecks = async (
    params: {
      offset?: number;
      limit?: number;
    } = {}
  ): Promise<{ data: BlacklistedDeckDto[]; totalItems: number } | null> => {
    error.value = null;
    try {
      return await $api<{ data: BlacklistedDeckDto[]; totalItems: number }>('difficulty-votes/blacklist', {
        query: {
          offset: params.offset ?? 0,
          limit: params.limit ?? 20,
        },
      });
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  return {
    error,
    fetchRating,
    fetchSuggestions,
    fetchCompletedDecks,
    fetchUnratedDecks,
    submitVote,
    skipPair,
    fetchStats,
    fetchMyVotes,
    fetchMySkipped,
    deleteVote,
    deleteSkip,
    submitRating,
    deleteRating,
    blockDeck,
    unblockDeck,
    fetchBlockedDecks,
  };
}
