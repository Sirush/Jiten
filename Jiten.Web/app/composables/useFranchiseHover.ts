import { nextTick, onBeforeUnmount, onMounted, ref, type Ref } from 'vue';

const POPOVER_WIDTH = 280;
const POPOVER_MARGIN = 8;

/** Hover/focus/tap state for franchise cards (marked with data-franchise-deck under root): the active card, its fixed-position popover, and scroll-to-and-flash. */
export function useFranchiseHover(root: Ref<HTMLElement | null>, options: { reveal?: (deckId: number) => void } = {}) {
  const activeNode = ref<number | null>(null);
  const popoverStyle = ref<Record<string, string>>({});
  const popoverRef = ref<unknown>(null);
  const flashNode = ref<number | null>(null);
  const isCoarsePointer = ref(false);

  let clearTimer: ReturnType<typeof setTimeout> | null = null;
  let flashTimer: ReturnType<typeof setTimeout> | null = null;
  let hoverScrollTimer: ReturnType<typeof setTimeout> | null = null;
  let coarseMq: MediaQueryList | null = null;

  function elementOf(el: unknown): HTMLElement | null {
    const node = (el as { $el?: unknown } | null)?.$el ?? el;
    return node instanceof HTMLElement ? node : null;
  }

  function nodeEl(id: number): HTMLElement | null {
    return root.value?.querySelector<HTMLElement>(`[data-franchise-deck="${id}"]`) ?? null;
  }

  function cancelClear() {
    if (clearTimer != null) {
      clearTimeout(clearTimer);
      clearTimer = null;
    }
  }

  // Delayed so the pointer can travel from the card onto the popover.
  function scheduleClear() {
    cancelClear();
    clearTimer = setTimeout(() => {
      activeNode.value = null;
    }, 200);
  }

  function clear() {
    cancelClear();
    activeNode.value = null;
  }

  function positionPopover(id: number) {
    if (!import.meta.client) return;
    const el = nodeEl(id);
    if (!el) return;
    const r = el.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const width = Math.min(POPOVER_WIDTH, vw - POPOVER_MARGIN * 2);
    let left = r.right + POPOVER_MARGIN;
    if (left + width > vw) left = r.left - POPOVER_MARGIN - width;
    left = Math.max(POPOVER_MARGIN, Math.min(left, vw - width - POPOVER_MARGIN));
    const top = Math.max(POPOVER_MARGIN, Math.min(r.top, vh - 160));
    popoverStyle.value = { left: `${left}px`, top: `${top}px`, width: `${width}px` };
  }

  function activate(id: number) {
    cancelClear();
    activeNode.value = id;
    positionPopover(id);
  }

  function onCardEnter(id: number) {
    if (!isCoarsePointer.value) activate(id);
  }

  function onCardLeave() {
    if (!isCoarsePointer.value) scheduleClear();
  }

  function onCardClick(e: MouseEvent, id: number) {
    if (!isCoarsePointer.value || activeNode.value === id) return;
    e.preventDefault();
    activate(id);
  }

  async function scrollToNode(id: number) {
    options.reveal?.(id);
    await nextTick();
    const el = nodeEl(id);
    if (!el) return;
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    el.scrollIntoView({ behavior: reduced ? 'auto' : 'smooth', block: 'center', inline: 'center' });
    flashNode.value = id;
    if (flashTimer != null) clearTimeout(flashTimer);
    flashTimer = setTimeout(() => {
      flashNode.value = null;
    }, 1600);
  }

  function onRowHover(id: number) {
    if (isCoarsePointer.value) return;
    cancelRowHover();
    hoverScrollTimer = setTimeout(() => scrollToNode(id), 150);
  }

  function cancelRowHover() {
    if (hoverScrollTimer != null) {
      clearTimeout(hoverScrollTimer);
      hoverScrollTimer = null;
    }
  }

  function onDocumentPointerDown(e: PointerEvent) {
    if (activeNode.value == null) return;
    const target = e.target as Node | null;
    if (!target || elementOf(popoverRef.value)?.contains(target)) return;
    if (nodeEl(activeNode.value)?.contains(target)) return;
    activeNode.value = null;
  }

  function onKeydown(e: KeyboardEvent) {
    if (e.key === 'Escape') clear();
  }

  const onCoarseChange = (e: MediaQueryListEvent) => {
    isCoarsePointer.value = e.matches;
  };

  onMounted(() => {
    coarseMq = window.matchMedia('(pointer: coarse)');
    isCoarsePointer.value = coarseMq.matches;
    coarseMq.addEventListener('change', onCoarseChange);
    document.addEventListener('pointerdown', onDocumentPointerDown);
    document.addEventListener('keydown', onKeydown);
  });

  onBeforeUnmount(() => {
    cancelClear();
    cancelRowHover();
    if (flashTimer != null) clearTimeout(flashTimer);
    coarseMq?.removeEventListener('change', onCoarseChange);
    document.removeEventListener('pointerdown', onDocumentPointerDown);
    document.removeEventListener('keydown', onKeydown);
  });

  return {
    activeNode,
    popoverStyle,
    popoverRef,
    flashNode,
    isCoarsePointer,
    nodeEl,
    cancelClear,
    scheduleClear,
    onCardEnter,
    onCardLeave,
    onCardClick,
    scrollToNode,
    onRowHover,
    cancelRowHover,
  };
}
