import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import { useRouter } from 'vue-router';
import type { CompleteGoogleRegistrationRequest, GoogleSignInResponse, GoogleRegistrationData, LoginRequest, TokenResponse } from '~/types/types';
import { TabSyncManager } from '~/utils/tabSync';
import { CookieMonitor, readCookie } from '~/utils/cookieMonitor';
import { useSrsStore } from '~/stores/srsStore';
import { useLegalStore } from '~/stores/legalStore';

const dbg = (...args: unknown[]) => {
  if (import.meta.dev) console.log(...args);
};

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
  const refreshStartedAt = ref<number>(0);
  let tabSyncManager: TabSyncManager | null = null;
  let cookieMonitor: CookieMonitor | null = null;

  // Temporary storage for Google registration flow
  const googleRegistrationData = ref<GoogleRegistrationData | null>(null);

  const isAuthenticated = computed(() => !!accessToken.value);
  const isAdmin = computed(() => user.value?.roles?.includes('Administrator') || false);

  const nuxtApp = useNuxtApp();
  const { $api } = nuxtApp;

  // Initialise tab synchronisation (client-side only)
  if (import.meta.client) {
    tabSyncManager = new TabSyncManager();
    const hasBroadcastChannel = typeof BroadcastChannel !== 'undefined';
    cookieMonitor = new CookieMonitor(hasBroadcastChannel);

    // Listen for token updates from other tabs
    tabSyncManager.on('TOKEN_REFRESHED', (payload) => {
      if (payload.accessToken && payload.refreshToken) {
        dbg('Token refreshed in another tab, syncing...');
        setTokens(payload.accessToken, payload.refreshToken);
        releaseRefreshLock();
      }
    });

    // Listen for refresh started events
    tabSyncManager.on('TOKEN_REFRESH_STARTED', (payload) => {
      dbg('Another tab started refreshing...');
      isRefreshing.value = true;
      refreshingTabId.value = payload.tabId;
      refreshStartedAt.value = Date.now();
    });

    // Listen for refresh failures
    tabSyncManager.on('TOKEN_REFRESH_FAILED', () => {
      dbg('Token refresh failed in another tab');
      clearAuthData();
      releaseRefreshLock();
    });

    // Listen for logout events
    tabSyncManager.on('LOGOUT', () => {
      dbg('User logged out in another tab');
      clearAuthData();
      const router = useRouter();
      router.push('/login');
    });

    // Monitor cookie changes (fallback mechanism)
    cookieMonitor.onChange((tokens) => {
      dbg('Cookie changed in another tab');

      // Only update if we're not currently refreshing
      if (!refreshLockActive()) {
        if (tokens.token && tokens.refreshToken) {
          adoptCookieTokens();
        } else if (!tokens.token && !tokens.refreshToken) {
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

    // Only the browser may empty the cookie jar. Nulling these during SSR emits a deletion
    // Set-Cookie on the HTML response, which drops the session in every open tab
    if (import.meta.client) {
      tokenCookie.value = null;
      refreshTokenCookie.value = null;
    }

    nuxtApp.runWithContext(() => {
      useJitenPlus().reset();
      useLegalStore().reset();
    });
  }

  function tokenExpiry(token: string | null | undefined): number {
    if (!token) return 0;
    try {
      return JSON.parse(atob(token.split('.')[1])).exp ?? 0;
    } catch {
      return 0;
    }
  }

  // Check if token is expired or about to expire (within 5 minutes)
  function isTokenExpired(token: string): boolean {
    const exp = tokenExpiry(token);
    if (!exp) return true; // Treat invalid tokens as expired
    // Consider token expired if it expires within 5 minutes (300 seconds)
    return exp < Math.floor(Date.now() / 1000) + 300;
  }

  /// The useCookie refs miss cookie-change events while a tab is frozen or discarded,
  function liveCookieTokens(): { token: string | null; refreshToken: string | null } {
    if (!import.meta.client) {
      return { token: tokenCookie.value || null, refreshToken: refreshTokenCookie.value || null };
    }
    return { token: readCookie('token'), refreshToken: readCookie('refreshToken') };
  }

  /// Adopts the cookie pair when another tab has issued a newer one
  function adoptCookieTokens(): boolean {
    const cookies = liveCookieTokens();
    if (!cookies.token || !cookies.refreshToken) return false;
    if (cookies.token === accessToken.value && cookies.refreshToken === refreshToken.value) return false;
    if (tokenExpiry(cookies.token) <= tokenExpiry(accessToken.value)) return false;

    dbg('Adopting newer tokens from the cookie jar');
    setTokens(cookies.token, cookies.refreshToken);
    return true;
  }

  /// A tab that misses the completion broadcast (frozen, or the refreshing tab was closed mid-flight)
  /// would otherwise treat the lock as held forever and never refresh again.
  const REFRESH_LOCK_TIMEOUT_MS = 15000;

  function refreshLockActive(): boolean {
    if (!isRefreshing.value) return false;
    if (Date.now() - refreshStartedAt.value < REFRESH_LOCK_TIMEOUT_MS) return true;
    releaseRefreshLock();
    return false;
  }

  function releaseRefreshLock() {
    isRefreshing.value = false;
    refreshingTabId.value = null;
    refreshStartedAt.value = 0;
  }

  async function refreshAccessToken(): Promise<boolean> {
    // Check if another tab is already refreshing
    if (refreshLockActive() && refreshingTabId.value !== tabSyncManager?.tabId) {
      dbg('Another tab is refreshing, waiting...');
      // Wait for the other tab to complete (max 10 seconds)
      const startTime = Date.now();
      while (refreshLockActive() && Date.now() - startTime < 10000) {
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
      adoptCookieTokens();
      // Check if we now have a valid token
      if (!!accessToken.value && !isTokenExpired(accessToken.value)) return true;
    }

    // Check if this tab is already refreshing
    if (refreshLockActive() && refreshingTabId.value === tabSyncManager?.tabId) {
      dbg('This tab is already refreshing, waiting...');
      while (refreshLockActive()) {
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
      return !!accessToken.value && !isTokenExpired(accessToken.value);
    }

    // Another tab may have already rotated the pair; its access token is usable as-is.
    if (adoptCookieTokens() && accessToken.value && !isTokenExpired(accessToken.value)) {
      return true;
    }

    if (!refreshToken.value) {
      dbg('No refresh token available');
      clearAuthData();
      return false;
    }

    isRefreshing.value = true;
    refreshingTabId.value = tabSyncManager?.tabId || null;
    refreshStartedAt.value = Date.now();

    // Notify other tabs that we're starting refresh
    tabSyncManager?.broadcast('TOKEN_REFRESH_STARTED', {
      tabId: tabSyncManager.tabId,
      timestamp: Date.now()
    });

    const postRefresh = (access: string | null, refresh: string) => $api<TokenResponse>('/auth/refresh', {
      method: 'POST',
      body: { accessToken: access, refreshToken: refresh },
    });

    try {
      dbg('Attempting to refresh token...');
      let data: TokenResponse;
      try {
        data = await postRefresh(accessToken.value, refreshToken.value);
      } catch (firstErr: any) {
        // The pair we hold can be a spent one this tab never saw rotated. The jar is authoritative,
        // so retry with what it holds before treating the rejection as a dead session.
        const firstStatus = firstErr?.status ?? firstErr?.statusCode ?? firstErr?.response?.status;
        const cookies = liveCookieTokens();
        const superseded = [400, 401, 403].includes(firstStatus)
                           && !!cookies.refreshToken
                           && cookies.refreshToken !== refreshToken.value;
        if (!superseded) throw firstErr;

        dbg('Refresh rejected with a superseded token, retrying with the cookie pair...');
        data = await postRefresh(cookies.token, cookies.refreshToken!);
      }

      if (data.accessToken && data.refreshToken) {
        setTokens(data.accessToken, data.refreshToken);

        // Broadcast new tokens to other tabs
        tabSyncManager?.broadcast('TOKEN_REFRESHED', {
          accessToken: data.accessToken,
          refreshToken: data.refreshToken,
          timestamp: Date.now()
        });

        dbg('Token refreshed successfully');
        return true;
      } else {
        throw new Error('Invalid refresh response');
      }
    } catch (err: any) {
      // Only a definitive auth rejection from the server means the tokens are dead.
      // /auth/refresh returns 400 (or 401/403 defensively) when the refresh token is
      // genuinely invalid/expired/used/revoked. Network errors, timeouts and 5xx
      // (502/503/504 — e.g. the API restarting during a deploy) are transient: keep the
      // tokens so the user can retry once the API is back instead of being logged out by
      // a brief blip. Applies on both client and server.
      const status = err?.status ?? err?.statusCode ?? err?.response?.status;
      const isAuthRejection = status === 400 || status === 401 || status === 403;

      // A rejection is expected whenever a returning visitor carries a stale refresh
      // token (very common during SSR) — log it quietly to avoid spamming server logs
      // with full FetchError stacks. Only surface genuinely unexpected/transient failures.
      if (isAuthRejection) {
        dbg('Token refresh rejected (stale/invalid refresh token):', status);
      } else {
        console.warn('Token refresh failed (transient):', status ?? err?.message ?? err);
      }

      if (isAuthRejection) {
        // Only tell other tabs to drop their session on a real rejection.
        tabSyncManager?.broadcast('TOKEN_REFRESH_FAILED', {
          timestamp: Date.now()
        });
        clearAuthData();
      }
      return false;
    } finally {
      releaseRefreshLock();
    }
  }

  function syncTokensFromCookies() {
    const cookies = liveCookieTokens();
    if (!accessToken.value && cookies.token) {
      accessToken.value = cookies.token;
    }
    if (!refreshToken.value && cookies.refreshToken) {
      refreshToken.value = cookies.refreshToken;
    }
    adoptCookieTokens();
  }

  async function ensureValidToken(): Promise<boolean> {
    syncTokensFromCookies();

    // If no access token at all
    if (!accessToken.value) {
      // dbg('No access token available');
      return false;
    }

    // If access token is expired or about to expire
    if (isTokenExpired(accessToken.value)) {
      dbg('Access token expired, attempting to refresh...');
      return await refreshAccessToken();
    }

    // dbg('Access token is valid');
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
    nuxtApp.runWithContext(() => {
      useSrsStore().refreshOverview(true);
      useJitenPlus().refresh();
    });
  }

  function initializeAuth() {
    dbg('Initializing auth...');

    if (tokenCookie.value) {
      accessToken.value = tokenCookie.value;
    }
    if (refreshTokenCookie.value) {
      refreshToken.value = refreshTokenCookie.value;
    }

    if (accessToken.value) {
      if (isTokenExpired(accessToken.value)) {
        dbg('Token expired on init, refreshing...');
        refreshAccessToken().then((success) => {
          if (success) {
            fetchCurrentUser().catch(() => {});
            onLoginSuccess();
          }
        });
      } else {
        dbg('Token valid on init, fetching user...');
        fetchCurrentUser().catch(() => {});
        onLoginSuccess();
      }
    } else {
      dbg('No token on init');
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
