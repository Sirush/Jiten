import type { SeriesKind, SeriesRef } from '~/types/series';
import { apiErrorMessage } from '~/utils/apiErrorMessage';

/** Admin create flow for a series or setting; `create` resolves to null after setting `error`. */
export function useSeriesCreate() {
  const { $api } = useNuxtApp();
  const error = ref('');
  const busy = ref(false);

  async function create(name: string, kind: SeriesKind, failMessage: string): Promise<SeriesRef | null> {
    const trimmed = name.trim();
    if (!trimmed) {
      error.value = 'Give it a name.';
      return null;
    }
    busy.value = true;
    error.value = '';
    try {
      return await $api<SeriesRef>('admin/series', { method: 'POST', body: { name: trimmed, kind } });
    } catch (e) {
      error.value = apiErrorMessage(e, failMessage);
      return null;
    } finally {
      busy.value = false;
    }
  }

  return { error, busy, create };
}
