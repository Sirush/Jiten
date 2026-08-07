export default defineNuxtPlugin((nuxtApp) => {
  const config = useRuntimeConfig();

  const isAuthUrl = (url: string) => url.includes('/auth/');
  const needsAuthHeader = (url: string) => url.includes('/auth/me') || url.includes('/auth/revoke-token');

  // ofetch retries PATCH/POST/PUT/DELETE zero times on its own; the guard keeps that true if its
  // definition of a payload method ever widens.
  const PAYLOAD_METHODS = new Set(['PATCH', 'POST', 'PUT', 'DELETE']);

  // 401 and 403 are deliberately absent: the token-refresh path below owns 401, and 403 carries the
  // Jiten+ gate payload. Retrying either would race the handler that already recovers it.
  const RETRY_STATUS_CODES = [408, 425, 429, 500, 502, 503, 504];

  const baseApi = $fetch.create({
    baseURL: config.public.baseURL,
    retryDelay: 500,
    retryStatusCodes: RETRY_STATUS_CODES,
    async onRequest({ request, options }) {
      const authStore = useAuthStore();

      options.headers = new Headers(options.headers);
      applySSRProxyHeaders(options.headers);

      // Only assign when unset. ofetch re-enters this hook on each retry carrying the decremented
      // count, and overwriting it there would loop forever.
      if (options.retry === undefined) {
        options.retry = import.meta.server || PAYLOAD_METHODS.has((options.method ?? 'GET').toUpperCase())
          ? 0
          : 2;
      }

      const url = request.toString();
      const isAuthEndpoint = isAuthUrl(url);

      // Renew an expiring token before sending, not after a 401: a recovered 401 still reaches the caller as an error.
      // Best-effort — a renewal that fails must not stop the request; the 401 path below still recovers it.
      if (import.meta.client && !isAuthEndpoint && authStore.accessToken) {
        try {
          await authStore.ensureValidToken();
        } catch {
          // ignored
        }
      }

      if (authStore.accessToken && (!isAuthEndpoint || needsAuthHeader(url))) {
        options.headers.set('Authorization', `Bearer ${authStore.accessToken}`);
      }
    },
    onResponseError({ response }) {
      if (response.status === 403 && import.meta.client && (response._data as { jitenPlus?: boolean } | undefined)?.jitenPlus === true) {
        void nuxtApp.runWithContext(() => useJitenPlus().refresh());
      }
    },
  });

  // The retry wraps the fetch rather than living in onResponseError, which ofetch runs for its side effects only: it discards the hook's return value and throws the original error regardless.
  const api = (async (request: Parameters<typeof baseApi>[0], options?: Parameters<typeof baseApi>[1]) => {
    try {
      return await baseApi(request, options);
    } catch (error: unknown) {
      const err = error as { status?: number; statusCode?: number; response?: { status?: number } };
      const status = err?.status ?? err?.statusCode ?? err?.response?.status;
      if (status !== 401 || !import.meta.client) throw error;

      const url = request.toString();
      const isAuthEndpoint = isAuthUrl(url);
      if (isAuthEndpoint && !needsAuthHeader(url)) throw error;

      const authStore = useAuthStore();

      // Never short-circuit on isRefreshing: concurrently 401ing requests would skip their retry and surface a spurious 401. refreshAccessToken() dedupes in-flight refreshes itself.
      if (await authStore.refreshAccessToken()) {
        return await baseApi(request, options);
      }

      // Bounce to /login only on a definitive rejection (tokens cleared); a transient failure leaves the refresh token in place to retry.
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

      throw error;
    }
  }) as unknown as typeof baseApi;

  return {
    provide: {
      api,
    },
  };
});
