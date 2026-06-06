export default defineNuxtPlugin((nuxtApp) => {
  const config = useRuntimeConfig();

  const api = $fetch.create({
    baseURL: config.public.baseURL,
    async onRequest({ request, options }) {
      const authStore = useAuthStore();

      options.headers = new Headers(options.headers);
      applySSRProxyHeaders(options.headers);

      // Extract URL to check if this is an auth endpoint
      const url = request.toString();
      const isAuthEndpoint = url.includes('/auth/');

      // Determine if this specific auth endpoint needs Authorization header
      // Most auth endpoints don't need it (login, register, refresh, etc.)
      // Only /auth/me and /auth/revoke-token require authentication
      const needsAuthHeader = url.includes('/auth/me') || url.includes('/auth/revoke-token');

      // Use token from authStore (reactive, always current) instead of re-reading cookies
      // This prevents stale reads during SSR when cookies have just been updated
      if (authStore.accessToken && (!isAuthEndpoint || needsAuthHeader)) {
        options.headers = options.headers || {};
        options.headers.set('Authorization', `Bearer ${authStore.accessToken}`);
      }
    },
    onResponse({ response }) {
      // Process response if needed
    },
    async onResponseError({ request, options, response }) {
      if (response.status === 401 && import.meta.client) {
        const authStore = useAuthStore();

        const url = request.toString();
        const isAuthEndpoint = url.includes('/auth/');
        const needsAuthHeader = url.includes('/auth/me') || url.includes('/auth/revoke-token');
        const shouldAttemptRefresh = !isAuthEndpoint || needsAuthHeader;

        if (shouldAttemptRefresh && !authStore.isRefreshing) {
          const refreshSuccess = await authStore.refreshAccessToken();

          if (refreshSuccess) {
            if (authStore.accessToken) {
              options.headers = options.headers || {};
              options.headers.set('Authorization', `Bearer ${authStore.accessToken}`);
            }

            try {
              const { onRequest: _, onResponse: _r, onResponseError: _e, ...retryOptions } = options;
              return await $fetch(request, retryOptions);
            } catch (retryError) {
              console.error('Retry after token refresh failed:', retryError);
            }
          }

          // Only bounce to /login when the refresh was definitively rejected (it cleared
          // the tokens). A transient failure (API down during a deploy) leaves the refresh
          // token in place — keep the user where they are and let it retry.
          if (!isAuthEndpoint && !authStore.refreshToken) {
            await nuxtApp.runWithContext(() => {
              const router = useRouter();
              const currentRoute = router.currentRoute.value.path;

              if (currentRoute !== '/login') {
                return navigateTo({
                  path: '/login',
                  query: { redirect: currentRoute },
                }, { external: true });
              }
            });
          }
        }
      }
    },
  });

  return {
    provide: {
      api,
    },
  };
});
