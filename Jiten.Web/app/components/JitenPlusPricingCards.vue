<script setup lang="ts">
  import type { JitenPlusPricingInfo } from '~/types';
  import { useToast } from 'primevue/usetoast';
  import { useAuthStore } from '~/stores/authStore';
  import { useLegalStore } from '~/stores/legalStore';

  const props = defineProps<{
    pricing: JitenPlusPricingInfo | null;
  }>();

  const { $api } = useNuxtApp();
  const auth = useAuthStore();
  const legal = useLegalStore();
  const toast = useToast();

  const { isFull, sources } = useJitenPlus();
  const isLifetime = computed(() => !!sources.value?.isLifetime);

  const lifetimeAvailable = computed(() => props.pricing?.lifetimeAvailable ?? true);
  const lifetimeWindowEndLabel = computed(() => {
    const raw = props.pricing?.lifetimeWindowEnd;
    if (!raw) return null;
    const d = new Date(raw);
    if (Number.isNaN(d.getTime())) return null;
    return d.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  });

  const showLifetimeNotice = computed(() => lifetimeAvailable.value && !!lifetimeWindowEndLabel.value && !isLifetime.value);
  const showLifetimeCard = computed(() => lifetimeAvailable.value || isLifetime.value);

  const checkingOut = ref<string | null>(null);

  type Plan = 'monthly' | 'yearly' | 'lifetime';

  const route = useRoute();
  const router = useRouter();

  const loginLink = (plan: Plan) => ({ path: '/login', query: { redirect: `/jiten-plus?plan=${plan}` } });

  // Resumes a checkout intent carried through login via ?plan=; the query is cleared so a refresh doesn't restart it.
  onMounted(async () => {
    const raw = Array.isArray(route.query.plan) ? route.query.plan[0] : route.query.plan;
    if (!raw || !auth.isAuthenticated || isFull.value) return;
    const plan = (['monthly', 'yearly', 'lifetime'] as Plan[]).find((p) => p === raw);
    if (!plan || (plan === 'lifetime' && !lifetimeAvailable.value)) return;
    await router.replace({ query: { ...route.query, plan: undefined } });
    await subscribe(plan);
  });

  // A sale needs recorded acceptance of the Terms of Sale; the API refuses checkout without it.
  const consentPlan = ref<Plan | null>(null);
  const consentTicked = ref(false);
  const consentEl = ref<HTMLElement | null>(null);

  async function subscribe(plan: Plan) {
    if (checkingOut.value) return;
    await legal.ensure();
    if (!legal.cgvAccepted) {
      consentPlan.value = plan;
      await nextTick();
      consentEl.value?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      return;
    }
    await startCheckout(plan);
  }

  async function confirmConsentAndCheckout() {
    if (!consentTicked.value || !consentPlan.value || checkingOut.value) return;
    const plan = consentPlan.value;
    try {
      await legal.acceptCgv();
    } catch {
      toast.add({ severity: 'error', summary: 'Something went wrong', detail: 'Could not record your acceptance. Please try again.', life: 6000 });
      return;
    }
    consentPlan.value = null;
    await startCheckout(plan);
  }

  async function startCheckout(plan: Plan) {
    checkingOut.value = plan;
    try {
      const result = await $api<{ url: string }>('/stripe/checkout', {
        method: 'POST',
        body: { plan },
      });
      window.location.href = result.url;
    } catch (e) {
      const error = (e as { data?: { error?: string } })?.data?.error || 'Could not start checkout. Please try again.';
      checkingOut.value = null;
      if (error === 'cgv-acceptance-required') {
        consentPlan.value = plan;
        await nextTick();
        consentEl.value?.scrollIntoView({ behavior: 'smooth', block: 'center' });
        return;
      }
      toast.add({ severity: 'error', summary: 'Checkout unavailable', detail: error, life: 6000 });
    }
  }
</script>

<template>
  <div>
    <section id="pricing" class="grid gap-5 mx-auto scroll-mt-20" :class="showLifetimeCard ? 'md:grid-cols-3 max-w-5xl' : 'md:grid-cols-2 max-w-3xl'">
      <!-- Monthly -->
      <div class="jp-card border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
        <h2 class="jp-card__name text-gray-900 dark:text-white">Monthly</h2>
        <div class="jp-card__price">
          <span class="jp-card__amount text-primary-600 dark:text-primary-300">€{{ JITEN_PLUS_PRICES.monthlyEur }}</span
          ><span class="jp-card__period text-gray-500 dark:text-gray-400">/ month</span>
        </div>
        <p class="jp-card__blurb text-gray-600 dark:text-gray-300">Stay flexible. Cancel anytime.</p>
        <ul class="jp-card__notes text-gray-600 dark:text-gray-300">
          <li>
            <Icon name="material-symbols:check-circle-outline-rounded" class="jp-card__note-icon" />
            Includes all Jiten+ benefits while your subscription is active.
          </li>
        </ul>
        <div class="jp-card__cta">
          <NuxtLink v-if="!auth.isAuthenticated" :to="loginLink('monthly')" class="block">
            <Button label="Choose monthly" severity="secondary" class="w-full" />
          </NuxtLink>
          <NuxtLink v-else-if="isFull" to="/settings/subscription" class="block">
            <Button label="Manage subscription" severity="secondary" outlined class="w-full" />
          </NuxtLink>
          <Button v-else label="Choose monthly" class="w-full" :loading="checkingOut === 'monthly'" @click="subscribe('monthly')" />
        </div>
      </div>

      <!-- Yearly (highlighted default) -->
      <div class="jp-card jp-card--featured border border-primary-400 bg-white dark:border-primary-500 dark:bg-gray-900">
        <span class="jp-card__ribbon">Best value · 2 months free</span>
        <h2 class="jp-card__name text-gray-900 dark:text-white">Yearly</h2>
        <div class="jp-card__price">
          <span class="jp-card__amount text-primary-600 dark:text-primary-300">€{{ JITEN_PLUS_PRICES.yearlyEur }}</span
          ><span class="jp-card__period text-gray-500 dark:text-gray-400">/ year</span>
        </div>
        <p class="jp-card__blurb text-gray-600 dark:text-gray-300">A full year for the price of 10 months.</p>
        <ul class="jp-card__notes text-gray-600 dark:text-gray-300">
          <li>
            <Icon name="material-symbols:check-circle-outline-rounded" class="jp-card__note-icon" />
            Includes all Jiten+ benefits while your subscription is active.
          </li>
        </ul>
        <div class="jp-card__cta">
          <NuxtLink v-if="!auth.isAuthenticated" :to="loginLink('yearly')" class="block">
            <Button label="Choose yearly" class="w-full" />
          </NuxtLink>
          <NuxtLink v-else-if="isFull" to="/settings/subscription" class="block">
            <Button label="Manage subscription" outlined class="w-full" />
          </NuxtLink>
          <Button v-else label="Choose yearly" class="w-full" :loading="checkingOut === 'yearly'" @click="subscribe('yearly')" />
        </div>
      </div>

      <!-- Lifetime -->
      <div
        v-if="showLifetimeCard"
        class="jp-card border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900"
        :class="{ 'jp-card--lifetime': lifetimeAvailable && !isLifetime }"
      >
        <span v-if="showLifetimeNotice" class="jp-card__ribbon jp-card__ribbon--amber">Limited offer · until {{ lifetimeWindowEndLabel }}</span>
        <h2 class="jp-card__name text-gray-900 dark:text-white">Lifetime</h2>
        <div class="jp-card__price">
          <span class="jp-card__amount text-primary-600 dark:text-primary-300">€{{ JITEN_PLUS_PRICES.lifetimeEur }}</span
          ><span class="jp-card__period text-gray-500 dark:text-gray-400">once</span>
        </div>
        <p class="jp-card__blurb text-gray-600 dark:text-gray-300">Pay once, access Jiten+ forever.</p>

        <template v-if="isFull && isLifetime">
          <div class="jp-card__cta">
            <div class="rounded-md bg-primary-50 dark:bg-primary-950/50 px-3 py-2 text-sm font-medium text-primary-700 dark:text-primary-300 text-center">
              You have lifetime access.
            </div>
          </div>
        </template>
        <template v-else>
          <ul class="jp-card__notes text-gray-600 dark:text-gray-300">
            <li>
              <Icon name="material-symbols:swap-horiz-rounded" class="jp-card__note-icon" />
              Already subscribed? Your prepaid subscription time is credited toward the lifetime price.
            </li>
          </ul>
          <div class="jp-card__cta">
            <NuxtLink v-if="!auth.isAuthenticated" :to="loginLink('lifetime')" class="block">
              <Button label="Get lifetime access" severity="warn" class="w-full" />
            </NuxtLink>
            <Button v-else label="Get lifetime access" severity="warn" class="w-full" :loading="checkingOut === 'lifetime'" @click="subscribe('lifetime')" />
          </div>
        </template>
      </div>
    </section>

    <div
      v-if="consentPlan"
      ref="consentEl"
      class="max-w-xl mx-auto mt-5 rounded-lg border border-primary-300 dark:border-primary-700 bg-white dark:bg-gray-900 p-4 shadow-sm"
    >
      <p class="text-sm font-medium text-gray-900 dark:text-white">One step before checkout</p>
      <div class="mt-2 flex items-start gap-2 text-sm text-gray-700 dark:text-gray-300">
        <Checkbox v-model="consentTicked" binary input-id="cgv-consent" />
        <label for="cgv-consent" class="cursor-pointer select-none">
          I have read and accept the
          <NuxtLink to="/cgv" target="_blank" class="underline hover:text-primary-600 dark:hover:text-primary-400">Terms of Sale</NuxtLink>
          <template v-if="legal.cgvVersion"> (version {{ legal.cgvVersion }})</template>
          — <NuxtLink to="/cgv-fr" target="_blank" class="underline hover:text-primary-600 dark:hover:text-primary-400">version française</NuxtLink>
        </label>
      </div>
      <Button
        :label="`Continue to ${consentPlan} checkout`"
        class="w-full mt-3 capitalize"
        :disabled="!consentTicked"
        :loading="!!checkingOut"
        @click="confirmConsentAndCheckout"
      />
    </div>

    <p class="text-center text-sm text-gray-600 dark:text-gray-300 mt-4">Cancel anytime. Cards, uploads, and lists are never deleted.</p>
    <p class="text-center text-xs text-gray-500 dark:text-gray-400 mt-1">Patreon and Ko-fi contributions are donation-only and don't include Jiten+.</p>
  </div>
</template>

<style scoped>
  /* Theme-dependent colours (background / border / text) live on the elements as Tailwind
     `dark:` utilities in the template. Scoped `:global(.dark-mode) .x` selectors are NOT used
     here: Vue's SFC compiler drops the descendant after a leading `:global()`, so those rules
     would silently target <html> instead of the card. This block keeps only structural styling. */
  .jp-card {
    position: relative;
    display: flex;
    flex-direction: column;
    border-radius: var(--radius-xl);
    padding: 1.5rem 1.25rem;
  }

  .jp-card--featured {
    box-shadow: 0 8px 30px rgba(99, 102, 241, 0.18);
  }

  @media (min-width: 768px) {
    .jp-card--featured {
      transform: scale(1.05);
      z-index: 1;
    }
  }

  .jp-card__ribbon {
    position: absolute;
    top: -0.7rem;
    left: 50%;
    transform: translateX(-50%);
    background: var(--p-primary-600);
    color: #fff;
    font-size: 0.7rem;
    font-weight: 700;
    padding: 0.15rem 0.7rem;
    border-radius: 9999px;
    white-space: nowrap;
  }

  .jp-card__ribbon--amber {
    background: var(--p-amber-500);
    color: var(--p-surface-900);
  }

  .jp-card--lifetime {
    border-color: var(--p-amber-400);
    animation: jp-lifetime-glow 3.5s ease-in-out infinite;
  }

  @keyframes jp-lifetime-glow {
    0%,
    100% {
      box-shadow: 0 0 12px rgba(245, 158, 11, 0.15);
    }
    50% {
      box-shadow: 0 0 26px rgba(245, 158, 11, 0.32);
    }
  }

  .jp-card--lifetime::before,
  .jp-card--lifetime::after {
    content: '✦';
    position: absolute;
    color: var(--p-amber-400);
    pointer-events: none;
    animation: jp-sparkle 2.6s ease-in-out infinite;
  }

  .jp-card--lifetime::before {
    top: 0.7rem;
    right: 0.9rem;
    font-size: 0.95rem;
  }

  .jp-card--lifetime::after {
    top: 4.6rem;
    right: 2rem;
    font-size: 0.65rem;
    animation-delay: 1.3s;
  }

  @keyframes jp-sparkle {
    0%,
    100% {
      opacity: 0.2;
      transform: scale(0.75) rotate(-8deg);
    }
    50% {
      opacity: 1;
      transform: scale(1.1) rotate(8deg);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .jp-card--lifetime {
      animation: none;
      box-shadow: 0 0 18px rgba(245, 158, 11, 0.22);
    }

    .jp-card--lifetime::before,
    .jp-card--lifetime::after {
      animation: none;
      opacity: 0.7;
    }
  }

  .jp-card__name {
    font-size: 1.1rem;
    font-weight: 600;
  }

  .jp-card__price {
    margin-top: 0.4rem;
    display: flex;
    align-items: baseline;
    gap: 0.3rem;
  }

  .jp-card__amount {
    font-size: 2rem;
    font-weight: 800;
  }

  .jp-card__period {
    font-size: 0.85rem;
  }

  .jp-card__blurb {
    margin-top: 0.6rem;
    font-size: 0.85rem;
    min-height: 2.4rem;
  }

  .jp-card__notes {
    margin-top: 0.75rem;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    font-size: 0.78rem;
  }

  .jp-card__notes li {
    display: flex;
    gap: 0.4rem;
    align-items: flex-start;
  }

  .jp-card__note-icon {
    flex-shrink: 0;
    margin-top: 0.1rem;
    color: var(--p-primary-500);
  }

  .jp-card__cta {
    margin-top: auto;
    padding-top: 1.1rem;
  }
</style>
