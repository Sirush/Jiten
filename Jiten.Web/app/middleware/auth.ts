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
      if (import.meta.server && authStore.refreshToken) {
        return;
      }
      authStore.clearAuthData();
      return navigateTo({
        path: '/login',
        query: { redirect: to.fullPath !== '/login' ? to.fullPath : undefined },
      });
    }
  } catch (error) {
    if (import.meta.server && authStore.refreshToken) {
      return;
    }
    authStore.clearAuthData();
    return navigateTo({
      path: '/login',
      query: { redirect: to.fullPath !== '/login' ? to.fullPath : undefined },
    });
  }
});
