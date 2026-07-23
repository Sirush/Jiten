export interface ReorderList {
  name: string;
  el: HTMLElement | null;
}

export interface ReorderPoint {
  list: string;
  index: number;
}

interface TouchReorderMultiOptions {
  /** Valid drop targets. The pointer is resolved against these on every move. */
  getLists: () => ReorderList[];
  onReorder: (from: ReorderPoint, to: ReorderPoint) => void;
  /** Touch lift delay; a move beyond the tolerance before it fires is treated as a scroll, not a drag. */
  longPressMs?: number;
  itemSelector?: string;
}

const MOVE_TOLERANCE = 8;
const EDGE = 64;
const MAX_SCROLL_SPEED = 16;

/**
 * Chooses the drop target. A target directly under the pointer always wins. Otherwise the nearest list
 * is only used when the drag originated from a real drop target (reordering an existing row, where a
 * release slightly outside should still land) — a drag from a source that is not itself a drop target
 * (e.g. a palette chip) must land inside a panel or resolve to nothing, so a stray release never inserts.
 */
export function pickDropList<T>(inside: T | null, nearest: T | null, sourceIsDropTarget: boolean): T | null {
  return inside ?? (sourceIsDropTarget ? nearest : null);
}

/**
 * Pointer-based reorder across multiple list containers (front/back panels). A drag can start from any
 * registered source (including one not in {@link TouchReorderMultiOptions.getLists}, e.g. a palette
 * chip); the drop target is resolved from the lists under the pointer. Touch drags lift after a short
 * long-press so page scrolling from the handle stays possible; mouse drags start immediately.
 */
export function useTouchReorderMulti(options: TouchReorderMultiOptions) {
  const itemSelector = options.itemSelector ?? '[data-reorder-item]';
  const longPressMs = options.longPressMs ?? 250;

  const isDragging = ref(false);
  // True for the duration of the click that immediately follows a completed drag, so a source that is
  // also clickable (a palette chip) does not both drop and fire its @click for a single gesture.
  const justDragged = ref(false);
  const fromPoint = ref<ReorderPoint | null>(null);
  const dropList = ref<string | null>(null);
  const dropIndex = ref<number | null>(null);

  let activePointerId: number | null = null;
  let ghost: HTMLElement | null = null;
  let sourceEl: HTMLElement | null = null;
  let scrollParent: HTMLElement | null = null;
  let offsetX = 0;
  let offsetY = 0;
  let startX = 0;
  let startY = 0;
  let lastClientX = 0;
  let lastClientY = 0;
  let liftTimer: ReturnType<typeof setTimeout> | null = null;
  let scrollRAF: number | null = null;
  let pointerIsTouch = false;

  function createGhost(clientX: number, clientY: number) {
    if (!sourceEl) return;
    const rect = sourceEl.getBoundingClientRect();
    offsetX = clientX - rect.left;
    offsetY = clientY - rect.top;
    const clone = sourceEl.cloneNode(true) as HTMLElement;
    clone.style.position = 'fixed';
    clone.style.left = `${rect.left}px`;
    clone.style.top = `${rect.top}px`;
    clone.style.width = `${rect.width}px`;
    clone.style.margin = '0';
    clone.style.zIndex = '9999';
    clone.style.pointerEvents = 'none';
    clone.style.opacity = '0.92';
    clone.style.boxShadow = '0 8px 24px rgba(0,0,0,0.22)';
    clone.style.transition = 'none';
    document.body.appendChild(clone);
    ghost = clone;
    moveGhost(clientX, clientY);
  }

  function moveGhost(clientX: number, clientY: number) {
    if (!ghost) return;
    ghost.style.left = `${clientX - offsetX}px`;
    ghost.style.top = `${clientY - offsetY}px`;
  }

  function resolveDrop(clientX: number, clientY: number) {
    const lists = options.getLists();
    const sourceIsDropTarget = lists.some((l) => l.name === fromPoint.value?.list);
    let inside: ReorderList | null = null;
    let nearest: ReorderList | null = null;
    let bestDist = Infinity;
    for (const l of lists) {
      if (!l.el) continue;
      const r = l.el.getBoundingClientRect();
      if (clientX >= r.left && clientX <= r.right && clientY >= r.top && clientY <= r.bottom) {
        inside = l;
        break;
      }
      const dx = clientX < r.left ? r.left - clientX : clientX > r.right ? clientX - r.right : 0;
      const dy = clientY < r.top ? r.top - clientY : clientY > r.bottom ? clientY - r.bottom : 0;
      const dist = Math.hypot(dx, dy);
      if (dist < bestDist) {
        bestDist = dist;
        nearest = l;
      }
    }
    const target = pickDropList(inside, nearest, sourceIsDropTarget);
    if (!target?.el) {
      dropList.value = null;
      dropIndex.value = null;
      return;
    }
    const items = [...target.el.querySelectorAll<HTMLElement>(itemSelector)].filter((el) => el !== sourceEl);
    let idx = items.length;
    for (let i = 0; i < items.length; i++) {
      const r = items[i].getBoundingClientRect();
      if (clientY < r.top + r.height / 2) {
        idx = i;
        break;
      }
    }
    dropList.value = target.name;
    dropIndex.value = idx;
  }

  // Nearest ancestor that actually scrolls vertically, so edge auto-scroll works inside a scrollable
  // container (e.g. the study-session settings Dialog body) and not only when the window scrolls.
  function findScrollParent(el: HTMLElement | null): HTMLElement | null {
    let node = el?.parentElement ?? null;
    while (node && node !== document.body && node !== document.documentElement) {
      const style = getComputedStyle(node);
      if ((style.overflowY === 'auto' || style.overflowY === 'scroll') && node.scrollHeight > node.clientHeight) return node;
      node = node.parentElement;
    }
    return null;
  }

  function tickScroll() {
    if (scrollParent) {
      const r = scrollParent.getBoundingClientRect();
      if (lastClientY < r.top + EDGE) {
        scrollParent.scrollTop -= MAX_SCROLL_SPEED * ((r.top + EDGE - lastClientY) / EDGE);
      } else if (lastClientY > r.bottom - EDGE) {
        scrollParent.scrollTop += MAX_SCROLL_SPEED * ((lastClientY - (r.bottom - EDGE)) / EDGE);
      }
    } else {
      const h = window.innerHeight;
      if (lastClientY < EDGE) {
        window.scrollBy(0, -MAX_SCROLL_SPEED * ((EDGE - lastClientY) / EDGE));
      } else if (lastClientY > h - EDGE) {
        window.scrollBy(0, MAX_SCROLL_SPEED * ((lastClientY - (h - EDGE)) / EDGE));
      }
    }
    resolveDrop(lastClientX, lastClientY);
    scrollRAF = requestAnimationFrame(tickScroll);
  }

  function lift(clientX: number, clientY: number) {
    isDragging.value = true;
    document.body.style.userSelect = 'none';
    scrollParent = findScrollParent(sourceEl);
    createGhost(clientX, clientY);
    resolveDrop(clientX, clientY);
    scrollRAF = requestAnimationFrame(tickScroll);
  }

  function clearLiftTimer() {
    if (liftTimer) {
      clearTimeout(liftTimer);
      liftTimer = null;
    }
  }

  function onMove(ev: PointerEvent) {
    if (ev.pointerId !== activePointerId) return;
    lastClientX = ev.clientX;
    lastClientY = ev.clientY;

    if (!isDragging.value) {
      // Pre-lift: below the tolerance nothing happens (allows a plain click/tap through). Past it, a
      // mouse begins dragging; a touch is taken to be a scroll and the pending drag is abandoned.
      if (Math.hypot(ev.clientX - startX, ev.clientY - startY) <= MOVE_TOLERANCE) return;
      if (pointerIsTouch) cancel();
      else lift(ev.clientX, ev.clientY);
      return;
    }
    ev.preventDefault();
    moveGhost(ev.clientX, ev.clientY);
    resolveDrop(ev.clientX, ev.clientY);
  }

  function onUp(ev: PointerEvent) {
    if (ev.pointerId !== activePointerId) return;
    const from = fromPoint.value;
    const toList = dropList.value;
    const toIndex = dropIndex.value;
    const wasDragging = isDragging.value;
    cancel();
    if (wasDragging) {
      // Set synchronously so the native click that follows this pointerup (on a clickable source such
      // as a palette chip) is suppressed; cleared on the next task, after that click has been dispatched.
      justDragged.value = true;
      setTimeout(() => {
        justDragged.value = false;
      }, 0);
    }
    if (wasDragging && from && toList !== null && toIndex !== null) {
      options.onReorder(from, { list: toList, index: toIndex });
    }
  }

  function cancel() {
    clearLiftTimer();
    document.removeEventListener('pointermove', onMove);
    document.removeEventListener('pointerup', onUp);
    document.removeEventListener('pointercancel', onUp);
    window.removeEventListener('blur', cancel);
    if (scrollRAF) {
      cancelAnimationFrame(scrollRAF);
      scrollRAF = null;
    }
    if (ghost) {
      ghost.remove();
      ghost = null;
    }
    document.body.style.userSelect = '';
    activePointerId = null;
    sourceEl = null;
    scrollParent = null;
    isDragging.value = false;
    fromPoint.value = null;
    dropList.value = null;
    dropIndex.value = null;
  }

  function handlePointerDown(e: PointerEvent, list: string, index: number) {
    if (e.button != null && e.button !== 0) return;
    if (activePointerId !== null) cancel();

    activePointerId = e.pointerId;
    pointerIsTouch = e.pointerType !== 'mouse';
    fromPoint.value = { list, index };
    sourceEl = (e.currentTarget as HTMLElement)?.closest<HTMLElement>(itemSelector) ?? (e.currentTarget as HTMLElement);
    startX = e.clientX;
    startY = e.clientY;
    lastClientX = e.clientX;
    lastClientY = e.clientY;

    document.addEventListener('pointermove', onMove, { passive: false });
    document.addEventListener('pointerup', onUp);
    document.addEventListener('pointercancel', onUp);
    window.addEventListener('blur', cancel);

    // Mouse lifts on the first move past the tolerance (so a plain click still fires); touch lifts after
    // a long-press unless the finger moves first (that move is a page scroll).
    if (pointerIsTouch) {
      liftTimer = setTimeout(() => {
        liftTimer = null;
        if (activePointerId === e.pointerId) lift(lastClientX, lastClientY);
      }, longPressMs);
    }
  }

  onUnmounted(cancel);

  return {
    isDragging: readonly(isDragging),
    justDragged: readonly(justDragged),
    fromPoint: readonly(fromPoint),
    dropList: readonly(dropList),
    dropIndex: readonly(dropIndex),
    handlePointerDown,
  };
}
