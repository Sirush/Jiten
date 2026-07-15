<script setup lang="ts">
  import RedeemPromoCode from '~/components/RedeemPromoCode.vue';
  import { useToast } from 'primevue/usetoast';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'Subscription - Settings - Jiten' });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const route = useRoute();
  const router = useRouter();

  const { tier, isFull, sources, quota, loading, refresh } = useJitenPlus();

  const tierBadgeTier = computed<'any' | 'full' | 'trial'>(() => {
    if (tier.value === 'full') return 'full';
    if (tier.value === 'trial') return 'trial';
    return 'any';
  });

  function formatDate(raw: string | null | undefined): string {
    if (!raw) return '';
    const d = new Date(raw);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  }

  const planName = computed(() => {
    const plan = sources.value?.plan;
    if (!plan) return 'active';
    return plan.toLowerCase();
  });

  // The badge already says "Jiten+ Trial", so this line only carries the days + caveat.
  const trialLine = computed(() => {
    if (tier.value !== 'trial') return null;
    const days = sources.value?.promoCreditDays ?? 0;
    return `${days} day${days === 1 ? '' : 's'} left — includes everything except permanent storage features.`;
  });

  const showCreditBreakdown = computed(() => {
    const s = sources.value;
    if (!s || s.credits.length === 0) return false;
    
    if (s.isLifetime) return false;
    if (s.subscriptionActive) return true;
    return s.credits.length > 1;
  });

  const isContributorLifetime = computed(() => sources.value?.lifetimeSource === 'ContributorGrant');

  const pausedNote = computed(() => {
    const s = sources.value;
    if (!s || s.isLifetime) return null;
    if (s.subscriptionActive && s.promoCreditDays > 0) {
      return `Your ${s.promoCreditDays} Jiten+ credit day${s.promoCreditDays === 1 ? '' : 's'} are paused while your subscription is active.`;
    }
    return null;
  });

  const thankYouMessages = computed(() =>
    (sources.value?.credits ?? [])
      .filter(c => !!c.thankYouMessage?.trim())
      .map(c => ({ id: c.userPromoCreditId, html: parseCustomMeaningHtml(c.thankYouMessage!) })),
  );

  const showSubscribeCta = computed(() => !sources.value?.subscriptionActive && !sources.value?.isLifetime);

  const quotaPercent = computed(() => {
    const q = quota.value;
    if (!q || q.maxBytes <= 0) return 0;
    return Math.min(100, Math.round((q.usedBytes / q.maxBytes) * 100));
  });

  function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    const units = ['KB', 'MB', 'GB', 'TB'];
    let value = bytes / 1024;
    let i = 0;
    while (value >= 1024 && i < units.length - 1) {
      value /= 1024;
      i++;
    }
    return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[i]}`;
  }
  const quotaLabel = computed(() => {
    const q = quota.value;
    if (!q) return '';
    return `${formatBytes(q.usedBytes)} used of ${formatBytes(q.maxBytes)}`;
  });

  const openingPortal = ref(false);
  async function openPortal() {
    if (openingPortal.value) return;
    openingPortal.value = true;
    try {
      const result = await $api<{ url: string }>('/stripe/portal', { method: 'POST' });
      window.location.href = result.url;
    } catch (e) {
      const error = (e as { data?: { error?: string } })?.data?.error || 'Could not open the billing portal.';
      toast.add({ severity: 'error', summary: 'Portal unavailable', detail: error, life: 6000 });
      openingPortal.value = false;
    }
  }

  const showTrialChecklist = ref(false);
  function onRedeemed(result: { grantsFullTier: boolean }) {
    refresh();
    // A full-granting code keeps the simple success message; a trial code shows the onboarding checklist.
    if (!result.grantsFullTier) showTrialChecklist.value = true;
  }

  // Checkout success can land here before the Stripe webhook has flipped the tier — poll briefly.
  async function pollForFull() {
    for (let i = 0; i < 3 && !isFull.value; i++) {
      await new Promise(r => setTimeout(r, 1500));
      await refresh();
    }
  }

  onMounted(async () => {
    if (route.query.checkout === 'success') {
      toast.add({
        severity: 'success',
        summary: 'Welcome to Jiten+',
        detail: 'Thank you for supporting Jiten!',
        life: 6000,
      });
      const query = { ...route.query };
      delete query.checkout;
      router.replace({ query });
      await refresh();
      if (!isFull.value) pollForFull();
    }
  });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 flex flex-col gap-4">
    <div class="flex items-center gap-2">
      <NuxtLink to="/settings">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded />
      </NuxtLink>
      <h1 class="text-2xl font-bold">Jiten+ subscription</h1>
    </div>

    <!-- Status -->
    <Card>
      <template #title>
        <div class="flex items-center gap-2">
          <h3 class="text-lg font-semibold">Status</h3>
          <JitenPlusBadge v-if="!loading && tier !== 'none'" :tier="tierBadgeTier" :link="false" />
          <Tag v-else-if="!loading" value="No active Jiten+" severity="secondary" />
        </div>
      </template>
      <template #content>
        <div v-if="loading" class="text-gray-500 dark:text-gray-400">Loading…</div>
        <div v-else>
          <ul class="space-y-2 text-gray-700 dark:text-gray-300">
            <!-- Lifetime is the strongest tier, so it leads. -->
            <li v-if="sources?.isLifetime" class="flex items-start gap-2">
              <Icon name="material-symbols:all-inclusive-rounded" class="text-primary-500 mt-0.5" />
              <span v-if="isContributorLifetime">Lifetime access, granted for contributing to Jiten. Thank you!</span>
              <span v-else>Lifetime access.</span>
            </li>
            <!-- With lifetime already active, a recurring subscription is redundant — say so plainly. -->
            <li v-if="sources?.subscriptionActive" class="flex items-start gap-2">
              <Icon
                :name="sources?.isLifetime ? 'material-symbols:info-outline-rounded' : 'material-symbols:autorenew-rounded'"
                class="mt-0.5"
                :class="sources?.isLifetime ? 'text-surface-400' : 'text-primary-500'"
              />
              <span v-if="sources?.isLifetime">
                You also have an active <span class="font-medium capitalize">{{ planName }}</span> subscription. Lifetime
                access already covers everything. You can cancel it below to avoid further charges.
              </span>
              <span v-else>
                Active <span class="font-medium capitalize">{{ planName }}</span> subscription<span v-if="sources?.periodEnd">,
                  renews on {{ formatDate(sources.periodEnd) }}</span
                >.
              </span>
            </li>
            <li v-if="trialLine" class="flex items-start gap-2">
              <Icon name="material-symbols:hourglass-top-rounded" class="text-primary-500 mt-0.5" />
              <span>{{ trialLine }}</span>
            </li>
            <li v-if="sources?.adminOverride" class="flex items-start gap-2">
              <Icon name="material-symbols:shield-person-outline-rounded" class="text-primary-500 mt-0.5" />
              <span>Access granted by an administrator.</span>
            </li>
            <li v-if="tier === 'none'" class="flex items-start gap-2">
              <Icon name="material-symbols:info-outline-rounded" class="text-surface-400 mt-0.5" />
              <span>You don't have an active Jiten+ subscription. Subscribe or redeem a code to unlock Jiten+ features.</span>
            </li>
          </ul>

          <!-- Promo credits (only when it adds detail beyond the status line) -->
          <div v-if="showCreditBreakdown" class="mt-4 border-t border-surface-200 dark:border-surface-700 pt-3">
            <h4 class="text-sm font-semibold text-surface-700 dark:text-surface-200 mb-1">Jiten+ credit</h4>
            <ul class="text-sm text-gray-600 dark:text-gray-400 space-y-1">
              <li v-for="credit in sources!.credits" :key="credit.userPromoCreditId" class="flex items-center gap-2">
                <Icon name="material-symbols:card-giftcard-rounded" class="text-primary-400 shrink-0" />
                {{ credit.remainingDays }} day{{ credit.remainingDays === 1 ? '' : 's' }} of
                {{ credit.grantsFullTier ? 'Full' : 'Trial' }}, granted {{ formatDate(credit.grantedAt) }}
              </li>
            </ul>
          </div>

          <Message v-if="pausedNote" severity="info" :closable="false" class="mt-3">{{ pausedNote }}</Message>

          <!-- Thank-you messages -->
          <div
            v-for="msg in thankYouMessages"
            :key="msg.id"
            class="mt-3 border-l-4 border-primary-500 pl-3 pr-2 py-2 bg-primary-50 dark:bg-primary-950/40 rounded-r"
          >
            <span class="text-xs tracking-wide text-primary-600 dark:text-primary-400 font-semibold">A note for you</span>
            <!-- eslint-disable-next-line vue/no-v-html -->
            <div class="break-words text-sm mt-0.5 text-gray-700 dark:text-gray-300" v-html="msg.html" />
          </div>

          <!-- Actions -->
          <div class="mt-5 flex flex-col sm:flex-row gap-2">
            <NuxtLink v-if="showSubscribeCta" to="/jiten-plus" class="w-full sm:w-auto">
              <Button label="Subscribe" icon="pi pi-star" class="w-full sm:w-auto" />
            </NuxtLink>
            <Button
              v-if="sources?.subscriptionActive"
              label="Manage subscription"
              icon="pi pi-credit-card"
              :loading="openingPortal"
              class="w-full sm:w-auto"
              @click="openPortal"
            />
          </div>
        </div>
      </template>
    </Card>

    <!-- Storage quota (Full only) -->
    <Card v-if="isFull && quota">
      <template #title>
        <h3 class="text-lg font-semibold">Card media storage</h3>
      </template>
      <template #content>
        <ProgressBar :value="quotaPercent" :show-value="false" class="!h-3" />
        <p class="text-sm text-gray-600 dark:text-gray-400 mt-2">{{ quotaLabel }}</p>
      </template>
    </Card>

    <!-- Redeem -->
    <Card>
      <template #title>
        <h3 class="text-lg font-semibold">Redeem a code</h3>
      </template>
      <template #content>
        <RedeemPromoCode @redeemed="onRedeemed" />
      </template>
    </Card>

    <JitenPlusTrialChecklist v-model:visible="showTrialChecklist" />
  </div>
</template>
