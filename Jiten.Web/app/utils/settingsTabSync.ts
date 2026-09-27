const resyncers = new Set<() => void>();
const otherTabListeners = new Set<() => void>();
let listening = false;

export function sameSetting(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

/** Same decoding as Nuxt's useCookie, so a value read from the jar compares equal to the ref's. */
export function readJarCookie(name: string): unknown {
  const prefix = `${name}=`;
  const raw = document.cookie
    .split('; ')
    .find((c) => c.startsWith(prefix))
    ?.slice(prefix.length);
  if (!raw) return undefined;
  const decoded = decodeURIComponent(raw);
  if (decoded === 'undefined') return undefined;
  try {
    const parsed = JSON.parse(decoded);
    return typeof parsed === 'number' && String(parsed) !== decoded ? decoded : parsed;
  } catch {
    return decoded;
  }
}

/** Sleeping tabs miss cross-tab messages, so every registered setting rereads its storage when the tab comes back. */
export function registerSettingResync(resync: () => void) {
  if (!import.meta.client) return;
  resyncers.add(resync);
  if (listening) return;
  listening = true;
  const resyncAll = () => resyncers.forEach((fn) => fn());
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') resyncAll();
  });
  window.addEventListener('focus', resyncAll);
}

/** Runs synchronously after each setting another tab changed, before this tab's watchers flush. */
export function onSettingChangedInOtherTab(listener: () => void): () => void {
  otherTabListeners.add(listener);
  return () => otherTabListeners.delete(listener);
}

export function settingChangedInOtherTab() {
  otherTabListeners.forEach((listener) => listener());
}
