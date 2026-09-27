import { useAuthStore } from '~/stores/authStore';
import { useJitenStore } from '~/stores/jitenStore';
import { useDisplayProfileStore } from '~/stores/displayProfileStore';
import { WORD_STATE_COLOUR_KEYS } from '~/utils/wordState';

/** Word colours used to live in the watch page's local prefs; carry them over the first time the profile has none. */
function migrateWatchColours(jiten: ReturnType<typeof useJitenStore>) {
  if (jiten.stateColours !== null) return;
  try {
    const stored = JSON.parse(localStorage.getItem('jiten-watch-prefs') ?? 'null');
    const colours = stored?.colours;
    if (!colours || typeof colours !== 'object') return;
    const picked = Object.fromEntries(WORD_STATE_COLOUR_KEYS.filter((k) => k in colours).map((k) => [k, colours[k]]));
    if (Object.keys(picked).length > 0) jiten.stateColours = { ...jiten.resolvedStateColours, ...picked };
  } catch {
    // A corrupt entry just means the defaults stand.
  }
}

export default defineNuxtPlugin((nuxtApp) => {
  nuxtApp.hook('app:mounted', () => {
    const auth = useAuthStore();
    const jiten = useJitenStore();
    const profiles = useDisplayProfileStore();

    migrateWatchColours(jiten);

    watch(
      () => (auth.isAuthenticated && auth.user ? (auth.user.id ?? auth.user.email ?? true) : null),
      (signedIn, previous) => {
        if (signedIn) {
          if (previous && previous !== signedIn) profiles.reset();
          profiles.init().then(() => {
            if (!profiles.localOffer) return;
            // The offer itself lives on the display page; this makes sure the reader learns their settings just changed.
            nuxtApp.vueApp.config.globalProperties.$toast?.add({
              severity: 'info',
              summary: `Your “${profiles.activeProfile?.name ?? 'account'}” display profile is now in use`,
              detail: 'This browser had different display settings. You can keep them as a new profile in Settings → Display.',
              life: 12000,
            });
          });
        } else if (previous) {
          profiles.reset();
        }
      },
      { immediate: true }
    );

    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') profiles.refresh();
    });
  });
});
