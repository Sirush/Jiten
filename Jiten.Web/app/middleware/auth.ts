export default defineNuxtRouteMiddleware(async (to, from) => {
  const authStore = useAuthStore();
  const tokenCookie = useCookie('token');
  const refreshTokenCookie = useCookie('refreshToken');

  if (!tokenCookie.value && !refreshTokenCookie.value) {
    return navigateTo({
      path: '/login',
      query: { redirect: to.fullPath !== '/login' ? to.fullPath : undefined },
    });
  }

  try {
    const hasValidToken = await authStore.ensureValidToken();

    if (!hasValidToken) {
      // A refresh token still present means the refresh failed transiently (e.g. the API
      // was down during a deploy) rather than being rejected — refreshAccessToken only
      // clears it on a definitive 400/401/403. Keep the session and let it retry instead
      // of bouncing the user to /login over a brief outage. Applies on client and server.
      if (authStore.refreshToken) {
        return;
      }
      authStore.clearAuthData();
      return navigateTo({
        path: '/login',
        query: { redirect: to.fullPath !== '/login' ? to.fullPath : undefined },
      });
    }
    if (!authStore.user) {
      try {
        await authStore.fetchCurrentUser();
      } catch (err: any) {
        // Only a 401 means the token was rejected. A transient failure (5xx/network during
        // a deploy) must not destroy the session — keep the valid token and render; the
        // user object will load on a later request.
        const status = err?.status ?? err?.statusCode ?? err?.response?.status;
        if (status !== 401) {
          return;
        }
        authStore.clearAuthData();
        return navigateTo({
          path: '/login',
          query: { redirect: to.fullPath !== '/login' ? to.fullPath : undefined },
        });
      }
    }
  } catch (error) {
    // Don't tear down the session over a transient error while a refresh token survives.
    if (authStore.refreshToken) {
      return;
    }
    authStore.clearAuthData();
    return navigateTo({
      path: '/login',
      query: { redirect: to.fullPath !== '/login' ? to.fullPath : undefined },
    });
  }
});
