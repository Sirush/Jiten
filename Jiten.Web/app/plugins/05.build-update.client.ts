import { joinURL } from 'ufo';

const POLL_INTERVAL_MS = 15 * 60 * 1000;
const RESUME_CHECK_MIN_GAP_MS = 60 * 1000;
const RELOAD_TARGET_KEY = 'jiten:update-reload-target';

export default defineNuxtPlugin((nuxtApp) => {
  const config = useRuntimeConfig();
  const currentBuildId = config.app.buildId;
  const updateAvailable = useState('build-update-available', () => false);
  const reloading = useState('build-update-reloading', () => false);
  const router = useRouter();

  const latestUrl = joinURL(config.app.baseURL, config.app.buildAssetsDir, 'builds/latest.json');

  let ignoredId: string | null = null;
  try {
    const target = sessionStorage.getItem(RELOAD_TARGET_KEY);
    sessionStorage.removeItem(RELOAD_TARGET_KEY);
    if (target && target !== currentBuildId) ignoredId = target;
  } catch {}

  let latestId: string | null = null;
  let lastCheck = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  async function check() {
    if (updateAvailable.value || !navigator.onLine) return;
    lastCheck = Date.now();
    try {
      const res = await fetch(`${latestUrl}?${lastCheck}`, { cache: 'no-store' });
      if (!res.ok) return;
      const meta = (await res.json()) as { id?: string };
      if (meta.id && meta.id !== currentBuildId && meta.id !== ignoredId) {
        latestId = meta.id;
        updateAvailable.value = true;
      }
    } catch {}
  }

  function schedule() {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      if (document.visibilityState === 'visible') await check();
      if (!updateAvailable.value) schedule();
    }, POLL_INTERVAL_MS);
  }

  // Mobile browsers suspend timers in background tabs, so returning to the tab is the check that matters most there.
  function onResume() {
    if (document.visibilityState !== 'visible' || Date.now() - lastCheck < RESUME_CHECK_MIN_GAP_MS) return;
    check();
  }

  function reloadInto(path?: string) {
    reloading.value = true;
    try {
      if (latestId) sessionStorage.setItem(RELOAD_TARGET_KEY, latestId);
    } catch {}
    if (path) window.location.assign(joinURL(config.app.baseURL, path));
    else window.location.reload();
  }

  // Swap builds on the next page change, where a full load costs nothing; query-only changes stay client-side.
  router.beforeResolve((to, from) => {
    // Leaving study already passed its own leave confirm; a full load would ask a second time via beforeunload.
    if (!updateAvailable.value || to.path === from.path || from.path === '/srs/study') return;
    reloadInto(to.fullPath);
    return false;
  });

  nuxtApp.hook('app:mounted', () => {
    schedule();
    document.addEventListener('visibilitychange', onResume);
    window.addEventListener('online', onResume);
  });

  return {
    provide: {
      reloadForUpdate: () => reloadInto(),
    },
  };
});
