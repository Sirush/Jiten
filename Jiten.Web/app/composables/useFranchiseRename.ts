import type { FranchiseSummary, MediaGroupTitles } from '~/types';
import { apiErrorMessage } from '~/utils/apiErrorMessage';
import { sameTitles, titlesDraft, titlesFromDraft } from '~/utils/groupTitles';

interface RenameTarget {
  franchiseId: number;
  titles: MediaGroupTitles;
  nameIsManual: boolean;
}

/** Admin franchise rename form; saving null titles returns the franchise to automatic naming. */
export function useFranchiseRename(handlers: { saved: (titles: MediaGroupTitles | null) => unknown; close: () => unknown }) {
  const { $api } = useNuxtApp();
  const value = ref(titlesDraft());
  const error = ref('');
  const busy = ref(false);

  function reset(titles: MediaGroupTitles) {
    value.value = titlesDraft(titles);
    error.value = '';
  }

  async function save(franchiseId: number, titles: MediaGroupTitles | null) {
    busy.value = true;
    error.value = '';
    try {
      await $api<FranchiseSummary>(`admin/franchise/${franchiseId}`, { method: 'PATCH', body: titles ?? { originalTitle: null } });
      await handlers.saved(titles);
    } catch (e) {
      error.value = apiErrorMessage(e, 'The name was not saved.');
    } finally {
      busy.value = false;
    }
  }

  function submit(target: RenameTarget) {
    const titles = titlesFromDraft(value.value);
    if (!titles.originalTitle) {
      error.value = 'Give it an original title, or use the automatic name.';
      return;
    }
    if (sameTitles(titles, target.titles) && target.nameIsManual) {
      handlers.close();
      return;
    }
    save(target.franchiseId, titles);
  }

  return { value, error, busy, reset, save, submit };
}
