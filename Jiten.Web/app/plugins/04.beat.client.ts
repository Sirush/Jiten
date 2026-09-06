export default defineNuxtPlugin((nuxtApp) => {
  const config = useRuntimeConfig();
  const local = location.hostname === 'localhost' || location.hostname === '127.0.0.1';
  if (local && !config.public.beatOnLocalhost) return;

  const authStore = useAuthStore();
  beatStart({ userId: () => authStore.user?.id });

  const route = useRoute();
  let lastPath = '';
  nuxtApp.hook('page:finish', () => {
    if (route.path === lastPath) return;
    lastPath = route.path;
    window.setTimeout(() => {
      beatView({
        path: route.path,
        route: route.matched[route.matched.length - 1]?.path ?? String(route.name ?? ''),
        title: document.title,
        search: location.search,
      });
    }, 150);
  });

  nuxtApp.hook('vue:error', (error) => beatError('vue', error));
});
