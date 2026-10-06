export function useMenuFocus() {
  const menu = ref<HTMLElement | null>(null);
  let trigger: HTMLElement | null = null;

  function rememberTrigger(event: Event) {
    if (event.currentTarget instanceof HTMLElement) trigger = event.currentTarget;
  }

  function restoreFocus() {
    const active = document.activeElement;
    if (!active || active === document.body || menu.value?.contains(active)) trigger?.focus({ preventScroll: true });
  }

  async function focusList() {
    await nextTick();
    const target = menu.value?.querySelector<HTMLElement>('[aria-checked="true"]') ?? menu.value?.querySelector<HTMLElement>('button');
    target?.focus({ preventScroll: true });
  }

  return { menu, rememberTrigger, restoreFocus, focusList };
}
