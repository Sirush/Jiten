export default defineNuxtPlugin((nuxtApp) => {
  const config = useRuntimeConfig();
  const authStore = useAuthStore();
  const route = useRoute();
  const routePattern = () => route.matched[route.matched.length - 1]?.path ?? String(route.name ?? '');

  // Sign-up attribution needs the landing page even where beats are switched off.
  let firstTouchChecked = false;
  nuxtApp.hook('page:finish', () => {
    if (firstTouchChecked) return;
    firstTouchChecked = true;
    if (!authStore.isAuthenticated) recordFirstTouch(routePattern());
  });

  const local = location.hostname === 'localhost' || location.hostname === '127.0.0.1';
  if (local && !config.public.beatOnLocalhost) return;

  beatStart({ userId: () => authStore.user?.id });

  let lastPath = '';
  nuxtApp.hook('page:finish', () => {
    if (route.path === lastPath) return;
    lastPath = route.path;
    window.setTimeout(() => {
      beatView({
        path: route.path,
        route: routePattern(),
        title: document.title,
        search: location.search,
      });
    }, 150);
  });

  nuxtApp.hook('vue:error', (error) => beatError('vue', error));
});
