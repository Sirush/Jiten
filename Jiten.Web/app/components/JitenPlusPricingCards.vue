<script setup lang="ts">
  import type { JitenPlusPricingInfo } from '~/types';
  import { useToast } from 'primevue/usetoast';
  import { useAuthStore } from '~/stores/authStore';

  const props = defineProps<{
    pricing: JitenPlusPricingInfo | null;
  }>();

  const { $api } = useNuxtApp();
  const auth = useAuthStore();
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

  async function subscribe(plan: 'monthly' | 'yearly' | 'lifetime') {
    if (checkingOut.value) return;
    checkingOut.value = plan;
    try {
      const result = await $api<{ url: string }>('/stripe/checkout', {
        method: 'POST',
        body: { plan },
      });
      window.location.href = result.url;
    } catch (e) {
      const error = (e as { data?: { error?: string } })?.data?.error || 'Could not start checkout. Please try again.';
      toast.add({ severity: 'error', summary: 'Checkout unavailable', detail: error, life: 6000 });
      checkingOut.value = null;
    }
  }
</script>

<template>
  <div>
    <section id="pricing" class="grid gap-5 mx-auto scroll-mt-20" :class="showLifetimeCard ? 'md:grid-cols-3 max-w-5xl' : 'md:grid-cols-2 max-w-3xl'">
      <!-- Monthly -->
      <div class="jp-card border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
        <h2 class="jp-card__name text-gray-900 dark:text-white">Monthly</h2>
        <div class="jp-card__price"><span class="jp-card__amount text-primary-600 dark:text-primary-300">€5</span><span class="jp-card__period text-gray-500 dark:text-gray-400">/ month</span></div>
        <p class="jp-card__blurb text-gray-600 dark:text-gray-300">Stay flexible. Cancel anytime.</p>
        <ul class="jp-card__notes text-gray-600 dark:text-gray-300">
          <li>
            <Icon name="material-symbols:check-circle-outline-rounded" class="jp-card__note-icon" />
            Includes all Jiten+ benefits while your subscription is active.
          </li>
        </ul>
        <div class="jp-card__cta">
          <NuxtLink v-if="!auth.isAuthenticated" to="/login" class="block">
            <Button label="Log in to choose monthly" severity="secondary" class="w-full" />
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
        <div class="jp-card__price"><span class="jp-card__amount text-primary-600 dark:text-primary-300">€50</span><span class="jp-card__period text-gray-500 dark:text-gray-400">/ year</span></div>
        <p class="jp-card__blurb text-gray-600 dark:text-gray-300">A full year for the price of 10 months.</p>
        <ul class="jp-card__notes text-gray-600 dark:text-gray-300">
          <li>
            <Icon name="material-symbols:check-circle-outline-rounded" class="jp-card__note-icon" />
            Includes all Jiten+ benefits while your subscription is active.
          </li>
        </ul>
        <div class="jp-card__cta">
          <NuxtLink v-if="!auth.isAuthenticated" to="/login" class="block">
            <Button label="Log in to choose yearly" class="w-full" />
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
        <div class="jp-card__price"><span class="jp-card__amount text-primary-600 dark:text-primary-300">€150</span><span class="jp-card__period text-gray-500 dark:text-gray-400">once</span></div>
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
            <NuxtLink v-if="!auth.isAuthenticated" to="/login" class="block">
              <Button label="Log in to get lifetime" severity="warn" class="w-full" />
            </NuxtLink>
            <Button v-else label="Get lifetime access" severity="warn" class="w-full" :loading="checkingOut === 'lifetime'" @click="subscribe('lifetime')" />
          </div>
        </template>
      </div>
    </section>
    <p class="text-center text-sm text-gray-600 dark:text-gray-300 mt-4">
      Cancel anytime. Cards, uploads, and lists are never deleted.
    </p>
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
    border-radius: 0.9rem;
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
