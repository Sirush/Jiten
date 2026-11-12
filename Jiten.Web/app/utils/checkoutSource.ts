const PLUS_ENTRY_KEY = 'jiten.plusEntry';
const PENDING_CHECKOUT_KEY = 'jiten.pendingCheckout';

const PASS_THROUGH_ROUTES = new Set([
  '/jiten-plus',
  '/login',
  '/register',
  '/google-registration',
  '/forgot-password',
  '/reset-password',
  '/confirm-email',
  '/welcome',
]);

export type PricingSection = 'marketing' | 'member';

export interface PlusEntryInput {
  src?: string;
  backRoute?: string;
  stored?: string;
  referrerHost: string;
}

/** A kept entry outranks the referrer, so a return from sign-in or from Stripe keeps the surface that started it. */
export function resolvePlusEntry(input: PlusEntryInput): string {
  const src = input.src?.trim().slice(0, 60);
  if (src) return src;
  if (input.backRoute && !PASS_THROUGH_ROUTES.has(input.backRoute)) return input.backRoute.slice(0, 120);
  if (input.stored) return input.stored;
  if (input.referrerHost) return `ref:${input.referrerHost}`;
  return 'direct';
}

export function rememberPlusEntry(entry: string): void {
  if (import.meta.server) return;
  try {
    sessionStorage.setItem(PLUS_ENTRY_KEY, entry);
  } catch {}
}

export function readPlusEntry(): string | undefined {
  if (import.meta.server) return undefined;
  try {
    return sessionStorage.getItem(PLUS_ENTRY_KEY) ?? undefined;
  } catch {
    return undefined;
  }
}

export interface CheckoutAttribution {
  plan: string;
  source: string;
  section: PricingSection;
}

/** Session storage carries this to checkout_completed: Stripe returns in the same tab, and the success URL is fixed server-side. */
export function markPendingCheckout(attribution: CheckoutAttribution): void {
  if (import.meta.server) return;
  try {
    sessionStorage.setItem(PENDING_CHECKOUT_KEY, JSON.stringify(attribution));
  } catch {}
}

export function parseCheckoutAttribution(raw: string | null): Record<string, string> | undefined {
  if (!raw) return undefined;
  try {
    const value = JSON.parse(raw) as Partial<CheckoutAttribution> | null;
    if (!value || typeof value !== 'object') return undefined;
    const props: Record<string, string> = {};
    for (const key of ['plan', 'source', 'section'] as const) {
      const field = value[key];
      if (typeof field === 'string' && field) props[key] = field;
    }
    return Object.keys(props).length ? props : undefined;
  } catch {
    return undefined;
  }
}

export function takePendingCheckout(): Record<string, string> | undefined {
  if (import.meta.server) return undefined;
  try {
    const raw = sessionStorage.getItem(PENDING_CHECKOUT_KEY);
    sessionStorage.removeItem(PENDING_CHECKOUT_KEY);
    return parseCheckoutAttribution(raw);
  } catch {
    return undefined;
  }
}
