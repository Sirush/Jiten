import type { Ref } from 'vue';

const STORAGE_KEY = 'jiten-list-anchor';
const FLASH_CLASS = 'list-anchor-flash';
const FLASH_DURATION_MS = 1200;
// Rows above the anchor carry contain-intrinsic-size estimates until they paint, so the first
// scroll lands short. Realigning on the following frames converges without a layout thrash.
// Long enough to outlast the router's own scroll-to-top and the reflow as rows above the
// anchor swap their contain-intrinsic-size estimate for their real height.
const SETTLE_MS = 700;
const RESTORE_DEADLINE_MS = 6000;
const TAKEOVER_EVENTS = ['wheel', 'touchstart', 'keydown'] as const;

interface StoredAnchor {
  anchor: string;
  path: string;
}

/** Sets the row a later visit to <paramref>path</paramref> should scroll back to. */
export function rememberListAnchor(anchor: string, path: string) {
  if (!import.meta.client) return;
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify({ anchor, path } satisfies StoredAnchor));
  } catch {
    // Private browsing or a full quota: losing the anchor is not worth breaking navigation over.
  }
}

/**
 * Restores the reading position of a paginated list by row identity rather than pixel offset:
 * row heights vary and progressive rendering invalidates a saved scrollTop.
 * Pass the rendered rows so a restore pending on not-yet-mounted DOM retries as batches land.
 */
export function useListAnchor(items: Ref<unknown[]>, attribute = 'data-list-anchor') {
  const route = useRoute();

  let pending: string | null = null;
  let deadline = 0;

  /** Records the clicked row, ignoring in-row controls so only navigation sets an anchor. */
  const rememberFromEvent = (event: MouseEvent) => {
    const target = event.target as HTMLElement | null;
    if (!target?.closest('a')) return;
    const anchor = target.closest(`[${attribute}]`)?.getAttribute(attribute);
    if (anchor) rememberListAnchor(anchor, route.fullPath);
  };

  const takePending = (): string | null => {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      const stored = JSON.parse(raw) as Partial<StoredAnchor>;
      if (!stored?.anchor || stored.path !== route.fullPath) return null;
      sessionStorage.removeItem(STORAGE_KEY);
      return stored.anchor;
    } catch {
      return null;
    }
  };

  const flash = (element: Element) => {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    element.classList.add(FLASH_CLASS);
    setTimeout(() => element.classList.remove(FLASH_CLASS), FLASH_DURATION_MS);
  };

  /**
   * Holds the row centred for a short window instead of scrolling once: the router's own
   * scroll-to-top lands after this navigation's mount and would otherwise undo the restore.
   * Any real user input hands control straight back.
   */
  const holdCentred = (element: Element) => {
    const start = Date.now();
    let active = true;

    const release = () => {
      if (!active) return;
      active = false;
      for (const event of TAKEOVER_EVENTS) window.removeEventListener(event, release);
    };

    for (const event of TAKEOVER_EVENTS) window.addEventListener(event, release, { passive: true, once: true });

    const step = () => {
      if (!active) return;
      element.scrollIntoView({ block: 'center' });
      if (Date.now() - start > SETTLE_MS) release();
      else requestAnimationFrame(step);
    };

    requestAnimationFrame(step);
  };

  const tryRestore = async () => {
    const anchor = pending;
    if (!anchor) return;
    if (Date.now() > deadline) {
      pending = null;
      return;
    }

    await nextTick();
    const element = document.querySelector(`[${attribute}="${CSS.escape(anchor)}"]`);
    if (!element) return;

    pending = null;
    holdCentred(element);
    flash(element);
  };

  if (import.meta.client) {
    onMounted(() => {
      pending = takePending();
      deadline = Date.now() + RESTORE_DEADLINE_MS;
      tryRestore();
    });

    watch(items, () => tryRestore());
  }

  return { rememberFromEvent };
}
