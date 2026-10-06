const GUARD_STATE_KEY = 'jitenMouseNavigationGuard';

function onGuardEntry(): boolean {
  return window.history.state?.[GUARD_STATE_KEY] === true;
}

/** Keeps a same-URL history entry on top for mouse back to land on, for browsers that navigate despite preventDefault (Vivaldi on Linux). */
export function useMouseNavigationGuard(isActive: () => boolean) {
  // Reusing an existing guard entry keeps remounts and repeated arming from stacking duplicates.
  function arm() {
    if (!isActive() || onGuardEntry()) return;
    window.history.pushState({ ...window.history.state, [GUARD_STATE_KEY]: true }, '', window.location.href);
  }

  /** Drops an unused guard entry. Calling it while a mouse back navigation is pending would navigate twice. */
  function release() {
    if (onGuardEntry()) window.history.back();
  }

  onMounted(() => {
    window.addEventListener('popstate', arm);
    arm();
  });

  onUnmounted(() => {
    window.removeEventListener('popstate', arm);
  });

  return { arm, release };
}
