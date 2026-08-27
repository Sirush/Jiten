import type { DifficultyRankingSectionDto } from '~/types/types';
import type { DifficultyRankingMoveMode, MediaTypeGroup } from '~/types';

export function useDifficultyRankings() {
  const { $api } = useNuxtApp();
  const error = ref<Error | null>(null);

  const fetchRankings = async (group?: MediaTypeGroup): Promise<DifficultyRankingSectionDto[]> => {
    error.value = null;
    try {
      return (
        (await $api<DifficultyRankingSectionDto[]>('difficulty-rankings', {
          query: group !== undefined ? { group } : undefined,
        })) ?? []
      );
    } catch (e) {
      error.value = e as Error;
      return [];
    }
  };

  const moveRanking = async (params: {
    deckId: number;
    mode: DifficultyRankingMoveMode;
    targetGroupId?: number;
    insertIndex?: number;
  }): Promise<DifficultyRankingSectionDto[]> => {
    error.value = null;
    try {
      return (
        (await $api<DifficultyRankingSectionDto[]>('difficulty-rankings/move', {
          method: 'POST',
          body: params,
        })) ?? []
      );
    } catch (e) {
      error.value = e as Error;
      return [];
    }
  };

  return {
    error,
    fetchRankings,
    moveRanking,
  };
}
