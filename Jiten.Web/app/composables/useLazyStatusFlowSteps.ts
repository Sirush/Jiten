import type { Component } from 'vue';

const component = shallowRef<Component | null>(null);
let pending: Promise<Component> | null = null;

function load(): Promise<Component> {
  pending ??= import('~/components/StatusFlowSteps.vue').then(
    (m) => (component.value = markRaw(m.default)),
    (error) => {
      pending = null;
      throw error;
    }
  );
  return pending;
}

/** StatusFlowSteps pulls in DatePicker; its popover opens only after the chunk loads so deck pages don't ship it to every visitor. */
export function useLazyStatusFlowSteps() {
  let opening = false;

  async function openWithSteps(event: Event, open: (event: Event) => void) {
    if (component.value) return open(event);
    if (opening) return;
    const anchor = popoverAnchorEvent(event);
    opening = true;
    try {
      await load();
    } finally {
      opening = false;
    }
    open(anchor);
  }

  return { component: computed(() => component.value), openWithSteps };
}
