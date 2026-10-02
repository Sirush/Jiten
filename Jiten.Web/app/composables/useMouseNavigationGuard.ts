/** Prevent browser back/forward while a mouse binding is active. */
export function useMouseNavigationGuard(isActive: () => boolean): () => void {
  let lockedUrl: string | null = null;

  function sync() {
    if (!isActive()) {
      lockedUrl = null;
      return;
    }
    if (lockedUrl !== null) return;
    lockedUrl = window.location.href;
    window.history.pushState({ ...window.history.state, jitenMouseBinding: true }, '', lockedUrl);
  }

  function handlePopState() {
    if (!isActive()) return;
    sync();
    if (lockedUrl !== null) window.history.pushState({ ...window.history.state, jitenMouseBinding: true }, '', lockedUrl);
  }

  onMounted(() => {
    window.addEventListener('popstate', handlePopState);
    sync();
  });

  onUnmounted(() => {
    window.removeEventListener('popstate', handlePopState);
    lockedUrl = null;
  });

  return sync;
}
