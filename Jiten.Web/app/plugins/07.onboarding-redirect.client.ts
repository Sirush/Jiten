import { useAuthStore } from '~/stores/authStore';

/** Sends a new account that never opened setup, and is missing the basics, from the home page to /welcome once. */
export default defineNuxtPlugin((nuxtApp) => {
  nuxtApp.hook('app:mounted', () => {
    const auth = useAuthStore();
    const router = useRouter();
    const { refresh } = useOnboarding();
    let checkedFor: string | null = null;

    watch(
      () => [auth.isAuthenticated && auth.user ? String(auth.user.id) : null, router.currentRoute.value.path] as const,
      async ([userKey, path]) => {
        if (!userKey || path !== '/' || checkedFor === userKey) return;
        checkedFor = userKey;
        try {
          const state = await refresh();
          if (!state?.shouldPrompt) return;
          if (router.currentRoute.value.path !== '/') return;
          await router.push('/welcome');
        } catch {
          checkedFor = null;
        }
      },
      { immediate: true }
    );
  });
});
