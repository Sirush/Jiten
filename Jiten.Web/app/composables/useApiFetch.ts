import type { PaginatedResponse } from '~/types/types';
import type { AsyncDataRequestStatus, UseFetchOptions } from '#app';

function revalidateOnClientAfterSsr(
  authStore: ReturnType<typeof useAuthStore>,
  execute: (opts?: any) => Promise<void>,
  data: Ref<unknown>,
  error: Ref<Error | null | undefined>,
): void {
  if (!import.meta.client || !authStore.isAuthenticated) return;

  const nuxtApp = useNuxtApp();
  if (!nuxtApp.isHydrating) return;

  authStore.ensureValidToken().then((valid) => {
    if (!valid) return;
    execute().then(() => {
      // Background revalidation of already-rendered SSR data. If it fails (e.g. the
      // API is mid-deploy) but we still hold the server-rendered payload, keep showing
      // the stale page instead of flipping the UI into an error state.
      if (error.value && data.value != null) {
        error.value = undefined;
      }
    });
  });
}

function setup401ErrorHandler(
  error: Ref<Error | null | undefined>,
  execute: (opts?: any) => Promise<void>,
  request: string | (() => string),
  authStore: ReturnType<typeof useAuthStore>
): void {
  if (!import.meta.client) return;

  const isHandling401 = ref(false);

  watch(error, async (newError) => {
    if (!newError || isHandling401.value) return;

    const fetchError = newError as any;
    const is401 = fetchError.status === 401 || fetchError.statusCode === 401;

    if (!is401) return;

    isHandling401.value = true;

    try {
      const url = typeof request === 'function' ? request() : request;
      if (url.includes('/auth/')) return;

      if (!authStore.isRefreshing) {
        const refreshSuccess = await authStore.refreshAccessToken();
        if (!refreshSuccess) {
          navigateTo('/login');
          return;
        }
      } else {
        while (authStore.isRefreshing) {
          await new Promise(resolve => setTimeout(resolve, 100));
        }
        if (!authStore.isAuthenticated) {
          navigateTo('/login');
          return;
        }
      }

      error.value = undefined;
      await execute();
    } finally {
      isHandling401.value = false;
    }
  });
}

function buildFetchOptions(
  opts: any,
  authStore: ReturnType<typeof useAuthStore>,
  request: string | (() => string)
) {
  const tokenCheckPromise = import.meta.client && authStore.isAuthenticated
    ? authStore.ensureValidToken()
    : Promise.resolve(true);

  const key = generateRequestKey(request);
  const uniqueKey = `api-${key}-${safeStringifyQuery(opts?.query)}`;

  const headers = new Headers(opts?.headers || {});
  applySSRProxyHeaders(headers);

  if (authStore.accessToken) {
    headers.set('Authorization', `Bearer ${authStore.accessToken}`);
  }

  // Auto-retry transient failures (network blips, API restarting during a deploy) so a
  // brief outage self-heals silently. Only for idempotent reads — never replay a mutation.
  // 401 is intentionally excluded; it's handled by the token-refresh flow below.
  const method = (opts?.method ?? 'GET').toString().toUpperCase();
  const isIdempotent = method === 'GET' || method === 'HEAD';

  return {
    ...opts,
    headers,
    key: opts?.key ?? uniqueKey,
    server: opts?.server ?? true,
    lazy: opts?.lazy ?? false,
    retry: opts?.retry ?? (isIdempotent ? 2 : 0),
    retryDelay: opts?.retryDelay ?? 500,
    retryStatusCodes: opts?.retryStatusCodes ?? [408, 425, 429, 500, 502, 503, 504],
    async onRequest({ options }: any) {
      await tokenCheckPromise;
      if (authStore.accessToken) {
        options.headers.set('Authorization', `Bearer ${authStore.accessToken}`);
      }
    }
  };
}

export function useApiFetch<T>(
  request: string | (() => string),
  opts?: any
): {
  data: Ref<T | null | undefined>;
  status: Ref<AsyncDataRequestStatus>;
  error: Ref<Error | null | undefined>;
  refresh: (opts?: any) => Promise<void>;
  execute: (opts?: any) => Promise<void>;
} {
  const { revalidateOnClient, ...fetchOpts } = opts ?? {};
  const authStore = useAuthStore();
  const options = buildFetchOptions(fetchOpts, authStore, request);

  const { data, status, error, refresh, execute } = useFetch<T>(request, {
    baseURL: useRuntimeConfig().public.baseURL,
    ...options
  });

  setup401ErrorHandler(error, execute, request, authStore);

  if (revalidateOnClient) {
    revalidateOnClientAfterSsr(authStore, execute, data, error);
  }

  return { data, status, error, refresh, execute };
}

export  function useApiFetchPaginated<T>(
  request: string | (() => string),
  opts?: any
)  {
  const { revalidateOnClient, ...fetchOpts } = opts ?? {};
  const config = useRuntimeConfig();
  const authStore = useAuthStore();
  const options = buildFetchOptions(fetchOpts, authStore, request);

  const { data, status, error, refresh, execute } = useFetch<PaginatedResponse<T>>(request, {
    baseURL: config.public.baseURL,
    ...options,
    deep: false,
  });

  setup401ErrorHandler(error, execute, request, authStore);

  if (revalidateOnClient) {
    revalidateOnClientAfterSsr(authStore, execute, data, error);
  }

  return {
    data,
    status,
    error,
    refresh,
    execute,
  };
}

// Helper function to generate a safe key from request parameter
const generateRequestKey = (request: string | (() => string)) => {
  if (typeof request === 'string') {
    return request;
  } else if (typeof request === 'function') {
    try {
      return request();
    } catch (e) {
      return 'dynamic-request';
    }
  } else if (request && typeof request === 'object' && 'value' in request) {
    return String(request.value);
  } else {
    return String(request);
  }
};

// Helper function to safely stringify query parameters
const safeStringifyQuery = (query: any) => {
  if (!query || typeof query !== 'object') return '{}';

  try {
    // Convert reactive values to their actual values
    const plainQuery: Record<string, any> = {};
    for (const [key, value] of Object.entries(query)) {
      // Handle Vue refs and computed values
      if (value && typeof value === 'object' && 'value' in value) {
        plainQuery[key] = value.value;
      } else {
        plainQuery[key] = value;
      }
    }
    return JSON.stringify(plainQuery);
  } catch (e) {
    // Fallback if JSON.stringify still fails
    return Object.keys(query).sort().join('-');
  }
};
