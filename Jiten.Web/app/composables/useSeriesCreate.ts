import type { SeriesKind, SeriesRef } from '~/types/series';
import { apiErrorMessage } from '~/utils/apiErrorMessage';
import { titlesFromDraft, type GroupTitlesDraft } from '~/utils/groupTitles';

/** Admin create flow for a series or setting; `create` resolves to null after setting `error`. */
export function useSeriesCreate() {
  const { $api } = useNuxtApp();
  const error = ref('');
  const busy = ref(false);

  async function create(draft: GroupTitlesDraft, kind: SeriesKind, failMessage: string): Promise<SeriesRef | null> {
    const titles = titlesFromDraft(draft);
    if (!titles.originalTitle) {
      error.value = 'Give it an original title.';
      return null;
    }
    busy.value = true;
    error.value = '';
    try {
      return await $api<SeriesRef>('admin/series', { method: 'POST', body: { ...titles, kind } });
    } catch (e) {
      error.value = apiErrorMessage(e, failMessage);
      return null;
    } finally {
      busy.value = false;
    }
  }

  return { error, busy, create };
}
