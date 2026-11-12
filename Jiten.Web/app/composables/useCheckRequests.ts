export function useCheckRequests() {
  const { $api } = useNuxtApp();
  const toast = useToast();
  const { undoBatch } = useBatchUndo();
  const busy = ref(false);
  const expired = ref(false);

  async function post<T>(url: string, body: object, errorMessage = 'Something went wrong. Try again.'): Promise<T | null> {
    busy.value = true;
    try {
      return await $api<T>(url, { method: 'POST', body });
    } catch (error) {
      toast.add({ severity: 'error', detail: extractApiError(error, errorMessage), life: 5000 });
      if ((error as { status?: number }).status === 410) expired.value = true;
      return null;
    } finally {
      busy.value = false;
    }
  }

  async function undo(batchId: string, successMessage: string, errorMessage = 'Something went wrong. Try again.') {
    busy.value = true;
    try {
      await undoBatch(batchId);
      toast.add({ severity: 'success', detail: successMessage, life: 4000 });
      return true;
    } catch (error) {
      toast.add({ severity: 'error', detail: extractApiError(error, errorMessage), life: 5000 });
      return false;
    } finally {
      busy.value = false;
    }
  }

  return { busy, expired, post, undo };
}
