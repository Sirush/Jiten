import type { FranchiseSummary } from '~/types';
import { apiErrorMessage } from '~/utils/apiErrorMessage';

export const FRANCHISE_NAME_MAX_LENGTH = 200;

interface RenameTarget {
  franchiseId: number;
  name: string;
  nameIsManual: boolean;
}

/** Admin franchise rename form; saving a null name returns the franchise to automatic naming. */
export function useFranchiseRename(handlers: { saved: (name: string | null) => unknown; close: () => unknown }) {
  const { $api } = useNuxtApp();
  const value = ref('');
  const error = ref('');
  const busy = ref(false);

  function reset(name: string) {
    value.value = name;
    error.value = '';
  }

  async function save(franchiseId: number, name: string | null) {
    busy.value = true;
    error.value = '';
    try {
      await $api<FranchiseSummary>(`admin/franchise/${franchiseId}`, { method: 'PATCH', body: { name } });
      await handlers.saved(name);
    } catch (e) {
      error.value = apiErrorMessage(e, 'The name was not saved.');
    } finally {
      busy.value = false;
    }
  }

  function submit(target: RenameTarget) {
    const name = value.value.trim();
    if (!name) {
      error.value = 'Give it a name, or use the automatic one.';
      return;
    }
    if (name === target.name && target.nameIsManual) {
      handlers.close();
      return;
    }
    save(target.franchiseId, name);
  }

  return { value, error, busy, reset, save, submit };
}
