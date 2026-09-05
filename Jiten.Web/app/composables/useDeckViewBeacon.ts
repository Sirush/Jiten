import type { Ref } from 'vue';

export const DECK_VIEW_DWELL_MS = 10_000;

export function useDeckViewBeacon(deckId: Ref<string | number>) {
  if (import.meta.server) return;

  const { $api } = useNuxtApp();
  let timer: ReturnType<typeof setTimeout> | null = null;

  const arm = (id: string | number) => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      timer = null;
      $api(`media-deck/${id}/view`, { method: 'POST' }).catch(() => {});
    }, DECK_VIEW_DWELL_MS);
  };

  onMounted(() => arm(deckId.value));
  watch(deckId, (id) => arm(id));
  onBeforeUnmount(() => {
    if (timer) clearTimeout(timer);
    timer = null;
  });
}
