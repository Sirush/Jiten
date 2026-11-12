import type { CardBatchUndoResult } from '~/types/types';

export function useBatchUndo() {
  const { $api } = useNuxtApp();
  const undoingId = ref<string | null>(null);

  async function undoBatch(batchId: string) {
    undoingId.value = batchId;
    try {
      return await $api<CardBatchUndoResult>(`user/vocabulary/batches/${batchId}/undo`, { method: 'POST' });
    } finally {
      undoingId.value = null;
    }
  }

  return { undoingId, undoBatch };
}
