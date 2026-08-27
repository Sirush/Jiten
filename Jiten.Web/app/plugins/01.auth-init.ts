export default defineNuxtPlugin(async (nuxtApp) => {
  const authStore = useAuthStore();

  if (import.meta.server) {
    authStore.syncTokensFromCookies();

    if (authStore.isAuthenticated) {
      if (authStore.isTokenExpired(authStore.accessToken!)) {
        await authStore.ensureValidToken();
      }

      if (authStore.isAuthenticated && !authStore.isTokenExpired(authStore.accessToken!)) {
        try {
          await authStore.fetchCurrentUser();
        } catch (error: any) {
          // Token looked valid but API rejected it — try refreshing
          if (error?.status === 401 && authStore.refreshToken) {
            const refreshed = await authStore.refreshAccessToken();
            if (refreshed) {
              try {
                await authStore.fetchCurrentUser();
              } catch {}
            }
          }
        }
      }
    }
  } else if (!authStore.user) {
    try {
      await authStore.ensureValidToken();

      if (authStore.isAuthenticated) {
        await authStore.fetchCurrentUser();
      }
    } catch (error) {
      console.error('Error initialising auth in plugin:', error);
    }
  }
});
