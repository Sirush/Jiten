import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import { useRouter } from 'vue-router';
import type { CompleteGoogleRegistrationRequest, GoogleSignInResponse, GoogleRegistrationData, LoginRequest, TokenResponse } from '~/types/types';
import { TabSyncManager } from '~/utils/tabSync';
import { CookieMonitor } from '~/utils/cookieMonitor';
import { useSrsStore } from '~/stores/srsStore';

export const useAuthStore = defineStore('auth', () => {
  const tokenCookie = useCookie('token', {
    watch: true,
    maxAge: 60 * 60 * 24 * 7, // 7 days
    path: '/',
    domain: process.env.NODE_ENV === 'production' ? '.jiten.moe' : undefined,
    secure: process.env.NODE_ENV === 'production',
    sameSite: 'lax',
  });

  const refreshTokenCookie = useCookie('refreshToken', {
    watch: true,
    maxAge: 60 * 60 * 24 * 30, // 30 days
    path: '/',
    domain: process.env.NODE_ENV === 'production' ? '.jiten.moe' : undefined,
    secure: process.env.NODE_ENV === 'production',
    sameSite: 'lax',
  });

  const accessToken = ref<string | null>(tokenCookie.value || null);
  const refreshToken = ref<string | null>(refreshTokenCookie.value || null);
  const user = ref<any | null>(null);
  const isLoading = ref<boolean>(false);
  const error = ref<string | null>(null);
  const isRefreshing = ref<boolean>(false);
  const refreshingTabId = ref<string | null>(null);
  let tabSyncManager: TabSyncManager | null = null;
  let cookieMonitor: CookieMonitor | null = null;

  // Temporary storage for Google registration flow
  const googleRegistrationData = ref<GoogleRegistrationData | null>(null);

  const isAuthenticated = computed(() => !!accessToken.value);
  const isAdmin = computed(() => user.value?.roles?.includes('Administrator') || false);

  const { $api } = useNuxtApp();

  // Initialise tab synchronisation (client-side only)
  if (import.meta.client) {
    tabSyncManager = new TabSyncManager();
    const hasBroadcastChannel = typeof BroadcastChannel !== 'undefined';
    cookieMonitor = new CookieMonitor(hasBroadcastChannel);

    // Listen for token updates from other tabs
    tabSyncManager.on('TOKEN_REFRESHED', (payload) => {
      if (payload.accessToken && payload.refreshToken) {
        console.log('Token refreshed in another tab, syncing...');
        setTokens(payload.accessToken, payload.refreshToken);
        isRefreshing.value = false;
        refreshingTabId.value = null;
      }
    });

    // Listen for refresh started events
    tabSyncManager.on('TOKEN_REFRESH_STARTED', (payload) => {
      console.log('Another tab started refreshing...');
      isRefreshing.value = true;
      refreshingTabId.value = payload.tabId;
    });

    // Listen for refresh failures
    tabSyncManager.on('TOKEN_REFRESH_FAILED', () => {
      console.log('Token refresh failed in another tab');
      clearAuthData();
      isRefreshing.value = false;
      refreshingTabId.value = null;
    });

    // Listen for logout events
    tabSyncManager.on('LOGOUT', () => {
      console.log('User logged out in another tab');
      clearAuthData();
      const router = useRouter();
      router.push('/login');
    });

    // Monitor cookie changes (fallback mechanism)
    cookieMonitor.onChange(({ token, refreshToken }) => {
      console.log('Cookie changed in another tab');

      // Only update if we're not currently refreshing
      if (!isRefreshing.value) {
        if (token && refreshToken) {
          // Tokens were updated externally - sync state
          accessToken.value = token;
          refreshToken.value = refreshToken;
        } else if (!token && !refreshToken) {
          // Tokens were cleared externally - logout
          clearAuthData();
        }
      }
    });
  }

  function setTokens(newAccessToken: string, newRefreshToken: string) {
    accessToken.value = newAccessToken;
    refreshToken.value = newRefreshToken;

    // Set the token cookie for the API plugin
    tokenCookie.value = newAccessToken;
    refreshTokenCookie.value = newRefreshToken;
  }

  function clearAuthData() {
    accessToken.value = null;
    refreshToken.value = null;
    user.value = null;
    googleRegistrationData.value = null;

    tokenCookie.value = null;
    refreshTokenCookie.value = null;
  }

  // Check if token is expired or about to expire (within 5 minutes)
  function isTokenExpired(token: string): boolean {
    try {
      const payload = JSON.parse(atob(token.split('.')[1]));
      const currentTime = Math.floor(Date.now() / 1000);
      // Consider token expired if it expires within 5 minutes (300 seconds)
      return payload.exp < currentTime + 300;
    } catch (error) {
      console.error('Error parsing token:', error);
      return true; // Treat invalid tokens as expired
    }
  }

  async function refreshAccessToken(): Promise<boolean> {
    // Check if another tab is already refreshing
    if (isRefreshing.value && refreshingTabId.value !== tabSyncManager?.tabId) {
      console.log('Another tab is refreshing, waiting...');
      // Wait for the other tab to complete (max 10 seconds)
      const startTime = Date.now();
      while (isRefreshing.value && Date.now() - startTime < 10000) {
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
      // Check if we now have a valid token
      return !!accessToken.value && !isTokenExpired(accessToken.value);
    }

    // Check if this tab is already refreshing
    if (isRefreshing.value && refreshingTabId.value === tabSyncManager?.tabId) {
      console.log('This tab is already refreshing, waiting...');
      while (isRefreshing.value) {
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
      return !!accessToken.value && !isTokenExpired(accessToken.value);
    }

    // If the cookie is different, another tab has ALREADY refreshed the token.
    const currentCookieRefToken = refreshTokenCookie.value;
    const currentCookieToken = tokenCookie.value;

    if (currentCookieRefToken && currentCookieRefToken !== refreshToken.value) {
      console.log('Detected fresh token in cookies (Race condition avoided). Syncing state...');

      // Update local state to match the cookie
      accessToken.value = currentCookieToken;
      refreshToken.value = currentCookieRefToken;

      // We are now valid, no need to call API
      return true;
    }

    if (!refreshToken.value) {
      console.log('No refresh token available');
      clearAuthData();
      return false;
    }

    isRefreshing.value = true;
    refreshingTabId.value = tabSyncManager?.tabId || null;

    // Notify other tabs that we're starting refresh
    tabSyncManager?.broadcast('TOKEN_REFRESH_STARTED', {
      tabId: tabSyncManager.tabId,
      timestamp: Date.now()
    });

    try {
      console.log('Attempting to refresh token...');
      const data = await $api<TokenResponse>('/auth/refresh', {
        method: 'POST',
        body: {
          accessToken: accessToken.value,
          refreshToken: refreshToken.value,
        },
      });

      if (data.accessToken && data.refreshToken) {
        setTokens(data.accessToken, data.refreshToken);

        // Broadcast new tokens to other tabs
        tabSyncManager?.broadcast('TOKEN_REFRESHED', {
          accessToken: data.accessToken,
          refreshToken: data.refreshToken,
          timestamp: Date.now()
        });

        console.log('Token refreshed successfully');
        return true;
      } else {
        throw new Error('Invalid refresh response');
      }
    } catch (err: any) {
      console.error('Token refresh failed:', err);

      // Only a definitive auth rejection from the server means the tokens are dead.
      // /auth/refresh returns 400 (or 401/403 defensively) when the refresh token is
      // genuinely invalid/expired/used/revoked. Network errors, timeouts and 5xx
      // (502/503/504 — e.g. the API restarting during a deploy) are transient: keep the
      // tokens so the user can retry once the API is back instead of being logged out by
      // a brief blip. Applies on both client and server.
      const status = err?.status ?? err?.statusCode ?? err?.response?.status;
      const isAuthRejection = status === 400 || status === 401 || status === 403;

      if (isAuthRejection) {
        // Only tell other tabs to drop their session on a real rejection.
        tabSyncManager?.broadcast('TOKEN_REFRESH_FAILED', {
          timestamp: Date.now()
        });
        clearAuthData();
      }
      return false;
    } finally {
      isRefreshing.value = false;
      refreshingTabId.value = null;
    }
  }

  function syncTokensFromCookies() {
    if (!accessToken.value && tokenCookie.value) {
      accessToken.value = tokenCookie.value;
    }
    if (!refreshToken.value && refreshTokenCookie.value) {
      refreshToken.value = refreshTokenCookie.value;
    }
  }

  async function ensureValidToken(): Promise<boolean> {
    syncTokensFromCookies();

    // If no access token at all
    if (!accessToken.value) {
      // console.log('No access token available');
      return false;
    }

    // If access token is expired or about to expire
    if (isTokenExpired(accessToken.value)) {
      console.log('Access token expired, attempting to refresh...');
      return await refreshAccessToken();
    }

    // console.log('Access token is valid');
    return true;
  }

  async function login(credentials: LoginRequest) {
    isLoading.value = true;
    error.value = null;

    try {
      const data = await $api<TokenResponse | { userId: string }>('/auth/login', {
        method: 'POST',
        body: credentials,
      });

      if ('accessToken' in data && 'refreshToken' in data) {
        setTokens(data.accessToken, data.refreshToken);
        await new Promise((resolve) => setTimeout(resolve, 500));
        await fetchCurrentUser();
        onLoginSuccess();
      } else {
        throw new Error('Login failed: No token received.');
      }
      return true;
    } catch (err) {
      error.value = err.data?.message || err.message || 'Login failed.';
      clearAuthData();
      return false;
    } finally {
      isLoading.value = false;
    }
  }

  async function loginWithGoogle(idToken: string): Promise<boolean | 'requiresRegistration'> {
    isLoading.value = true;
    error.value = null;

    try {
      const data = await $api<GoogleSignInResponse>('/auth/signin-google', {
        method: 'POST',
        body: { idToken: idToken },
      });

      if (data.requiresRegistration) {
        // Store temp data for the registration flow (only once, do not call API again elsewhere)
        googleRegistrationData.value = {
          tempToken: data.tempToken || '',
          email: data.email || '',
          name: data.name || '',
          picture: data.picture,
          username: '',
        };
        return 'requiresRegistration';
      } else if (data.accessToken && data.refreshToken) {
        // Existing user - complete login
        setTokens(data.accessToken, data.refreshToken);
        await fetchCurrentUser();
        onLoginSuccess();
        return true;
      } else {
        throw new Error('Google login failed: Invalid response.');
      }
    } catch (err: any) {
      error.value = err.data?.message || err.message || 'Google login failed.';
      return false;
    } finally {
      isLoading.value = false;
    }
  }

  async function completeGoogleRegistration(registrationData: CompleteGoogleRegistrationRequest): Promise<boolean> {
    isLoading.value = true;
    error.value = null;

    try {
      const data = await $api<TokenResponse>('/auth/complete-google-registration', {
        method: 'POST',
        body: registrationData,
      });

      if (data.accessToken && data.refreshToken) {
        setTokens(data.accessToken, data.refreshToken);
        await fetchCurrentUser();
        onLoginSuccess();
        return true;
      } else {
        throw new Error('Registration failed: No tokens received.');
      }
    } catch (err: any) {
      error.value = err.data?.message || err.message || 'Registration failed.';
      return false;
    } finally {
      isLoading.value = false;
    }
  }

  async function fetchCurrentUser() {
    try {
      const data = await $api('/auth/me');
      user.value = data;
    } catch (err: any) {
      console.error('Failed to fetch current user:', err);
      user.value = null;
      throw err;
    }
  }

  async function logout() {
    isLoading.value = true;

    try {
      if (accessToken.value) {
        await $api('/auth/revoke-token', {
          method: 'POST',
        });
      }
    } catch (err) {
      console.error('Error revoking token:', err.data?.message || err.message);
    } finally {
      // Notify other tabs to logout
      tabSyncManager?.broadcast('LOGOUT', { timestamp: Date.now() });

      clearAuthData();
      isLoading.value = false;

      // Only redirect on client-side (router unavailable during SSR)
      if (import.meta.client) {
        const router = useRouter();
        router.push('/login');
      }
    }
  }

  function onLoginSuccess() {
    const srs = useSrsStore();
    srs.fetchEnrollment();
    srs.refreshOverview(true);
  }

  function initializeAuth() {
    console.log('Initializing auth...');

    if (tokenCookie.value) {
      accessToken.value = tokenCookie.value;
    }
    if (refreshTokenCookie.value) {
      refreshToken.value = refreshTokenCookie.value;
    }

    if (accessToken.value) {
      if (isTokenExpired(accessToken.value)) {
        console.log('Token expired on init, refreshing...');
        refreshAccessToken().then((success) => {
          if (success) {
            fetchCurrentUser().catch(() => {});
            onLoginSuccess();
          }
        });
      } else {
        console.log('Token valid on init, fetching user...');
        fetchCurrentUser().catch(() => {});
        onLoginSuccess();
      }
    } else {
      console.log('No token on init');
    }
  }

  return {
    // state
    accessToken,
    refreshToken,
    user,
    isLoading,
    error,
    googleRegistrationData,

    // getters
    isAuthenticated,
    isAdmin,
    isRefreshing,

    // actions
    syncTokensFromCookies,
    setTokens,
    clearAuthData,
    login,
    loginWithGoogle,
    completeGoogleRegistration,
    fetchCurrentUser,
    logout,
    initializeAuth,
    refreshAccessToken,
    ensureValidToken,

    // utilities
    isTokenExpired,

    // cleanup
    $dispose() {
      tabSyncManager?.destroy();
      cookieMonitor?.destroy();
    }
  };
});
