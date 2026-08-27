import type { PaginatedResponse } from '~/types/types';
import type { AsyncDataRequestStatus, UseFetchOptions } from '#app';

// Public shape of the fetch wrappers. These intentionally do NOT block on the request
// (so client-side navigation renders immediately and pages can show their own skeletons).
// `ready` resolves once the underlying fetch settles — await it only where a synchronous
// snapshot of the data is needed right after setup (e.g. server-side OG image props), so
// eager reads aren't empty. Note: `await useApiFetch(...)` itself is a no-op (the returned
// object is not a promise); use `ready` when you must wait.
type ApiFetchResult<T> = {
  data: Ref<T | null | undefined>;
  status: Ref<AsyncDataRequestStatus>;
  error: Ref<Error | null | undefined>;
  refresh: (opts?: any) => Promise<void>;
  execute: (opts?: any) => Promise<void>;
  ready: Promise<void>;
};

// `useFetch` accepts a reactive request, and several callers pass a computed URL. Anything reading
// the URL outside the fetch itself must go through this, or a ref stringifies to "[object Object]".
type ApiFetchRequest = string | (() => string) | Ref<string>;

function resolveRequest(request: ApiFetchRequest): string {
  return typeof request === 'function' ? request() : unref(request);
}

function unwrapQuery(query: Record<string, unknown> | undefined): Record<string, unknown> | undefined {
  if (!query) return undefined;
  const plain: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(query)) {
    plain[key] = unref(value);
  }
  return plain;
}

function revalidateOnClientAfterSsr(
  authStore: ReturnType<typeof useAuthStore>,
  request: ApiFetchRequest,
  query: Record<string, unknown> | undefined,
  data: Ref<unknown>,
  error: Ref<Error | null | undefined>
): void {
  if (!import.meta.client || !authStore.isAuthenticated) return;

  const nuxtApp = useNuxtApp();
  if (!nuxtApp.isHydrating) return;

  authStore.ensureValidToken().then(async (valid) => {
    if (!valid) return;
    try {
      // Out-of-band $api fetch instead of execute(): execute() flips status back to
      // 'pending' (re-showing skeletons over already-rendered SSR content) and always
      // replaces data with fresh object identities, which re-renders every list item
      // and cancels any pending lazy hydration. SSR requests carry the auth cookie,
      // so the revalidated payload is usually byte-identical — only replace when not.
      const api = nuxtApp.$api as (url: string, opts?: { query?: Record<string, unknown> }) => Promise<unknown>;
      const fresh = await api(resolveRequest(request), { query: unwrapQuery(query) });
      if (JSON.stringify(fresh) !== JSON.stringify(data.value)) {
        data.value = fresh;
      }
      // We hold a successfully fetched payload — clear any stale SSR error state.
      if (error.value && data.value != null) {
        error.value = undefined;
      }
    } catch {
      // Background revalidation of already-rendered SSR data. If it fails (e.g. the
      // API is mid-deploy) but we still hold the server-rendered payload, keep showing
      // the stale page instead of flipping the UI into an error state.
    }
  });
}

function setup401ErrorHandler(
  error: Ref<Error | null | undefined>,
  execute: (opts?: any) => Promise<void>,
  request: ApiFetchRequest,
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
      if (resolveRequest(request).includes('/auth/')) return;

      const refreshSuccess = await authStore.refreshAccessToken();
      if (!refreshSuccess) {
        if (!authStore.refreshToken) {
          navigateTo('/login');
        }
        return;
      }

      error.value = undefined;
      await execute();
    } finally {
      isHandling401.value = false;
    }
  });
}

function buildFetchOptions(opts: any, authStore: ReturnType<typeof useAuthStore>, request: ApiFetchRequest) {
  const tokenCheckPromise = import.meta.client && authStore.isAuthenticated ? authStore.ensureValidToken() : Promise.resolve(true);

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
    // Bound how long an SSR render may wait on the API. Without a timeout a slow API holds the
    // inbound page connection (and an outbound socket) open indefinitely; under load these pile up
    // until the web container hits its FD/connection ceiling and can no longer accept new connections
    // — including the localhost /healthz probe — so Coolify marks it unhealthy and Traefik serves the
    // 503 "no server available" page. A timeout degrades a single render (data stays null → the page's
    // skeleton/fallback, e.g. a placeholder OG image) instead of taking the whole container down. No
    // client timeout: the client shows skeletons and the user can wait or navigate away.
    timeout: opts?.timeout ?? (import.meta.server ? 8000 : undefined),
    // Never retry on the server — retries multiply held connections exactly when the API is already
    // slow, accelerating the exhaustion above. The client still retries transient blips (deploys etc.).
    retry: opts?.retry ?? (import.meta.server ? 0 : isIdempotent ? 2 : 0),
    retryDelay: opts?.retryDelay ?? 500,
    retryStatusCodes: opts?.retryStatusCodes ?? [408, 425, 429, 500, 502, 503, 504],
    async onRequest({ options }: any) {
      await tokenCheckPromise;
      if (authStore.accessToken) {
        options.headers.set('Authorization', `Bearer ${authStore.accessToken}`);
      }
    },
  };
}

export function useApiFetch<T>(request: ApiFetchRequest, opts?: any): ApiFetchResult<T> {
  const { revalidateOnClient, ...fetchOpts } = opts ?? {};
  const authStore = useAuthStore();
  const options = buildFetchOptions(fetchOpts, authStore, request);

  const result = useFetch<T>(request, {
    baseURL: useRuntimeConfig().public.baseURL,
    ...options,
  });

  setup401ErrorHandler(result.error, result.execute, request, authStore);

  if (revalidateOnClient) {
    revalidateOnClientAfterSsr(authStore, request, fetchOpts.query, result.data, result.error);
  }

  return {
    data: result.data,
    status: result.status,
    error: result.error,
    refresh: result.refresh,
    execute: result.execute,
    ready: Promise.resolve(result).then(
      () => undefined,
      () => undefined
    ),
  } as unknown as ApiFetchResult<T>;
}

export function useApiFetchPaginated<T>(request: ApiFetchRequest, opts?: any): ApiFetchResult<PaginatedResponse<T>> {
  const { revalidateOnClient, ...fetchOpts } = opts ?? {};
  const config = useRuntimeConfig();
  const authStore = useAuthStore();
  const options = buildFetchOptions(fetchOpts, authStore, request);

  const result = useFetch<PaginatedResponse<T>>(request, {
    baseURL: config.public.baseURL,
    ...options,
    deep: false,
  });

  setup401ErrorHandler(result.error, result.execute, request, authStore);

  if (revalidateOnClient) {
    revalidateOnClientAfterSsr(authStore, request, fetchOpts.query, result.data, result.error);
  }

  return {
    data: result.data,
    status: result.status,
    error: result.error,
    refresh: result.refresh,
    execute: result.execute,
    ready: Promise.resolve(result).then(
      () => undefined,
      () => undefined
    ),
  } as unknown as ApiFetchResult<PaginatedResponse<T>>;
}

// Helper function to generate a safe key from request parameter
const generateRequestKey = (request: ApiFetchRequest) => {
  try {
    return resolveRequest(request);
  } catch {
    return 'dynamic-request';
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
