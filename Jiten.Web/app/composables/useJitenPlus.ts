import { useAuthStore } from '~/stores/authStore';

export type JitenPlusTier = 'none' | 'trial' | 'full';

export type JitenPlusFeature = 'card-media' | 'freq-list-save' | 'freq-list-generate' | 'request-boosts';

export interface PromoCreditInfo {
  userPromoCreditId: number;
  remainingDays: number;
  grantsFullTier: boolean;
  grantedAt: string;
  thankYouMessage: string | null;
}

export interface JitenPlusSources {
  subscriptionActive: boolean;
  plan: string | null;
  periodEnd: string | null;
  isLifetime: boolean;
  lifetimeSource: string | null;
  promoCreditDays: number;
  credits: PromoCreditInfo[];
  adminOverride: boolean;
}

export interface JitenPlusQuota {
  usedBytes: number;
  maxBytes: number;
}

export interface JitenPlusStatus {
  tier: JitenPlusTier;
  sources: JitenPlusSources;
  quota: JitenPlusQuota;
}

// 'trial' means Trial-or-Full suffices; 'full' means the paid (storage) tier is required.
const FEATURE_TIERS: Record<JitenPlusFeature, 'trial' | 'full'> = {
  'card-media': 'full',
  'freq-list-save': 'full',
  'freq-list-generate': 'trial',
  'request-boosts': 'trial',
};

// Deduplicates concurrent first-fetches across the many gates that mount at once (client-only,
// so it is per-tab, never shared across SSR requests).
let inflight: Promise<void> | null = null;
let attempted = false;
let refetchOnFocusBound = false;

// Matches the API-side tier cache duration, so a focus refetch can actually observe a change.
const STALE_AFTER_MS = 60_000;

/**
 * Single source of truth for the viewer's Jiten+ tier. Status is fetched once and shared via
 * useState so every gate/badge reads the same state without refetching.
 */
export function useJitenPlus() {
  const auth = useAuthStore();
  const status = useState<JitenPlusStatus | null>('jitenplus-status', () => null);
  const loading = useState<boolean>('jitenplus-loading', () => false);
  const fetched = useState<boolean>('jitenplus-fetched', () => false);
  const fetchedAt = useState<number | null>('jitenplus-fetched-at', () => null);

  const tier = computed<JitenPlusTier>(() => (auth.isAuthenticated ? (status.value?.tier ?? 'none') : 'none'));
  const isFull = computed(() => tier.value === 'full');
  const isTrial = computed(() => tier.value === 'trial');
  const isPlus = computed(() => tier.value === 'full' || tier.value === 'trial');
  const sources = computed(() => status.value?.sources ?? null);
  const quota = computed(() => status.value?.quota ?? null);

  async function doFetch() {
    if (!import.meta.client || !auth.isAuthenticated) {
      fetched.value = true;
      return;
    }
    loading.value = true;
    try {
      const { $api } = useNuxtApp();
      status.value = await $api<JitenPlusStatus>('/jiten-plus/status');
      fetched.value = true;
      fetchedAt.value = Date.now();
    } catch {
      // Leave fetched false so a later mount or window focus retries, instead of pinning
      // the user at 'none' for the rest of the tab session after one transient failure.
      status.value = null;
    } finally {
      loading.value = false;
    }
  }

  function startFetch() {
    attempted = true;
    const p = doFetch().finally(() => {
      if (inflight === p) inflight = null;
    });
    inflight = p;
    return p;
  }

  async function refresh() {
    await (inflight ?? startFetch());
  }

  function reset() {
    status.value = null;
    loading.value = false;
    fetched.value = false;
    fetchedAt.value = null;
    inflight = null;
    attempted = false;
  }

  function ensure() {
    if (!import.meta.client || fetched.value || loading.value || inflight) return;
    startFetch();
  }

  // The tier can change server-side while a tab sits open (admin grant/revoke, subscription
  // change from another tab, promo expiry). Refetch on return-to-tab once the last successful
  // fetch is stale; `attempted` keeps tabs that never needed the status from fetching it.
  function refetchIfStale() {
    if (!auth.isAuthenticated || loading.value || inflight || !attempted) return;
    if (fetched.value && fetchedAt.value && Date.now() - fetchedAt.value < STALE_AFTER_MS) return;
    startFetch();
  }

  if (import.meta.client && !refetchOnFocusBound) {
    refetchOnFocusBound = true;
    window.addEventListener('focus', refetchIfStale);
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') refetchIfStale();
    });
  }

  // Kick off the lazy first fetch on first use.
  if (import.meta.client) ensure();

  function tierSatisfies(required: 'trial' | 'full'): boolean {
    return required === 'full' ? isFull.value : isPlus.value;
  }

  function hasFeature(feature: JitenPlusFeature): boolean {
    return tierSatisfies(FEATURE_TIERS[feature]);
  }

  return { tier, isFull, isTrial, isPlus, sources, quota, loading, refresh, reset, hasFeature, tierSatisfies };
}
