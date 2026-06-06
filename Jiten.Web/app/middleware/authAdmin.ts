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
      // Transient refresh failure (API down during a deploy) still leaves the refresh
      // token in place — only a definitive 400/401/403 clears it. Keep the session rather
      // than bouncing to /login over a brief outage. Applies on client and server.
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
        // Only a 401 means the token was rejected; a transient 5xx/network error must not
        // destroy the session. Keep the valid token and render.
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

    if (!authStore.isAdmin) {
      return navigateTo({
        path: '/',
        query: { redirect: to.fullPath !== '/' ? to.fullPath : undefined },
      });
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
