<script setup lang="ts">
  import type { BoostBalance } from '~/composables/useMediaRequests';

  import { useAuthStore } from '~/stores/authStore';

  const { tier, isFull, isTrial, sources, quota, limits } = useJitenPlus();
  const { fetchBoostBalance } = useMediaRequests();
  const auth = useAuthStore();

  const mediaListLink = computed(() => (auth.user?.userName ? `/profile/${auth.user.userName}/media` : '/profile'));

  const badgeTier = computed<'full' | 'trial'>(() => (isTrial.value ? 'trial' : 'full'));
  const isLifetime = computed(() => !!sources.value?.isLifetime);
  const isContributorLifetime = computed(() => sources.value?.lifetimeSource === 'ContributorGrant');

  function formatDate(raw: string | null | undefined): string {
    if (!raw) return '';
    const d = new Date(raw);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  }

  const statusLine = computed(() => {
    const s = sources.value;
    if (!s) return '';
    if (s.isLifetime) {
      return isContributorLifetime.value
        ? 'You have lifetime access, granted for contributing to Jiten. Thank you!'
        : 'You have lifetime access. Thank you for supporting Jiten!';
    }
    if (s.subscriptionActive) {
      if (s.cancelAtPeriodEnd) {
        const until = s.periodEnd ? ` Jiten+ stays fully available until ${formatDate(s.periodEnd)}.` : '';
        return `Your ${s.plan?.toLowerCase() ?? ''} subscription is cancelled and will not renew.${until}`;
      }
      const renew = s.periodEnd ? `, renews on ${formatDate(s.periodEnd)}` : '';
      return `Active ${s.plan?.toLowerCase() ?? ''} subscription${renew}. Thank you for supporting Jiten!`;
    }
    if (isTrial.value) {
      const days = s.promoCreditDays;
      return `Trial — ${days} day${days === 1 ? '' : 's'} left.`;
    }
    if (s.promoCreditDays > 0) {
      return `${s.promoCreditDays} day${s.promoCreditDays === 1 ? '' : 's'} of Jiten+ credit left.`;
    }
    if (s.adminOverride) return 'Access granted by an administrator.';
    return 'Your Jiten+ is active.';
  });

  const quotaPercent = computed(() => {
    const q = quota.value;
    if (!q || q.maxBytes <= 0) return 0;
    return Math.min(100, Math.round((q.usedBytes / q.maxBytes) * 100));
  });

  const quotaLabel = computed(() => {
    const q = quota.value;
    if (!q) return '';
    if (q.maxBytes <= 0) return `${formatBytes(q.usedBytes)} stored`;
    return `${formatBytes(q.usedBytes)} used of ${formatBytes(q.maxBytes)}`;
  });

  // Shown to Trial users as the reason to upgrade; hidden when the paid allowance isn't larger.
  const fullStorageLabel = computed(() => {
    const q = quota.value;
    const full = q?.allowances?.fullBytes;
    if (!q || !full || full <= q.maxBytes) return null;
    return formatBytes(full);
  });

  const boostBalance = ref<BoostBalance | null>(null);
  onMounted(async () => {
    boostBalance.value = await fetchBoostBalance();
  });

  const limitRows = computed(() => {
    const l = limits.value;
    return [
      { label: 'Study decks', value: l.studyDecks },
      { label: 'Words across word list decks', value: l.studyDeckWords },
      { label: 'Words per import', value: l.importWords },
      { label: 'Active media requests', value: l.activeMediaRequests },
      { label: 'Custom sentences per word', value: l.customSentencesPerWord },
    ].map((row) => ({ label: row.label, value: row.value.toLocaleString() }));
  });
</script>

<template>
  <div class="max-w-5xl mx-auto px-2">
    <!-- Status header -->
    <div class="flex flex-col md:flex-row md:items-end md:justify-between gap-4">
      <div>
        <JitenPlusBadge :tier="badgeTier" :link="false" class="!text-sm !px-3 !py-1 mb-3" />
        <h1 class="text-3xl font-bold text-gray-900 dark:text-white">Your Jiten+</h1>
        <p class="mt-2 text-gray-600 dark:text-gray-300 flex items-center gap-2">
          <Icon :name="tier === 'trial' ? 'material-symbols:hourglass-top-rounded' : 'material-symbols:favorite-rounded'" class="text-primary-500 shrink-0" />
          {{ statusLine }}
        </p>
      </div>
      <div class="flex flex-wrap gap-2">
        <NuxtLink to="/settings/subscription">
          <Button label="Manage subscription" icon="pi pi-credit-card" severity="secondary" outlined />
        </NuxtLink>
        <a v-if="!isLifetime" href="#pricing">
          <Button :label="isFull ? 'Upgrade plan' : 'Upgrade'" icon="pi pi-star" />
        </a>
      </div>
    </div>

    <Message v-if="isTrial" severity="info" :closable="false" class="mt-5">
      Your trial includes every Jiten+ feature with a smaller storage allowance. A paid plan
      <template v-if="fullStorageLabel">raises storage to {{ fullStorageLabel }} and</template>
      unlocks saved frequency lists.
    </Message>

    <!-- Feature tiles -->
    <div class="mt-8 grid gap-4 sm:grid-cols-2">
      <div class="jp-tile border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
        <div class="jp-tile__head">
          <Icon name="material-symbols:image-outline-rounded" class="jp-tile__icon" />
          <h3 class="jp-tile__title text-gray-900 dark:text-white">Card images &amp; audio</h3>
        </div>
        <p class="jp-tile__body text-gray-600 dark:text-gray-300">
          Add your own images and audio to any card from its vocabulary page or during reviews. Uploads stay yours even if you cancel.
        </p>
        <div class="mt-3">
          <ProgressBar :value="quotaPercent" :show-value="false" class="!h-2" />
          <p class="mt-1.5 text-xs text-gray-500 dark:text-gray-400">{{ quotaLabel }}</p>
        </div>
        <div class="jp-tile__actions">
          <NuxtLink to="/settings/card-media">
            <Button label="Manage uploads" size="small" severity="secondary" />
          </NuxtLink>
        </div>
      </div>

      <div class="jp-tile border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
        <div class="jp-tile__head">
          <Icon name="material-symbols:format-list-numbered-rounded" class="jp-tile__icon" />
          <h3 class="jp-tile__title text-gray-900 dark:text-white">Custom frequency lists</h3>
        </div>
        <p class="jp-tile__body text-gray-600 dark:text-gray-300">
          Build frequency lists from any media on Jiten, use them in Yomitan, and share them with a link.
          <template v-if="!isFull">Generating and downloading is included in your trial; saving lists needs a paid plan.</template>
        </p>
        <div class="jp-tile__actions">
          <NuxtLink to="/jiten-plus/frequency-lists">
            <Button label="Open list builder" size="small" severity="secondary" />
          </NuxtLink>
        </div>
      </div>

      <div class="jp-tile border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
        <div class="jp-tile__head">
          <Icon name="material-symbols:explore-rounded" class="jp-tile__icon" />
          <h3 class="jp-tile__title text-gray-900 dark:text-white">Immersion plans</h3>
        </div>
        <p class="jp-tile__body text-gray-600 dark:text-gray-300">
          Get a plan of what to read or watch next, picked from your own vocabulary and ordered to teach you the most new words.
        </p>
        <div class="jp-tile__actions">
          <NuxtLink to="/jiten-plus/immersion-plan">
            <Button label="Open immersion plans" size="small" severity="secondary" />
          </NuxtLink>
        </div>
      </div>

      <div class="jp-tile border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
        <div class="jp-tile__head">
          <Icon name="material-symbols:bolt-rounded" class="jp-tile__icon" />
          <h3 class="jp-tile__title text-gray-900 dark:text-white">Media request boosts</h3>
          <Tag
            v-if="boostBalance"
            :value="`${boostBalance.remaining} / ${boostBalance.limit} left this month`"
            :severity="boostBalance.remaining > 0 ? 'success' : 'secondary'"
            class="ml-auto"
          />
        </div>
        <p class="jp-tile__body text-gray-600 dark:text-gray-300">Prioritise any open media request. Each boost counts as 5 regular votes.</p>
        <div class="jp-tile__actions">
          <NuxtLink to="/requests">
            <Button label="Boost a request" size="small" severity="secondary" />
          </NuxtLink>
        </div>
      </div>

      <div class="jp-tile sm:col-span-2 border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
        <div class="jp-tile__head">
          <Icon name="material-symbols:show-chart-rounded" class="jp-tile__icon" />
          <h3 class="jp-tile__title text-gray-900 dark:text-white">Your coverage journey</h3>
        </div>
        <p class="jp-tile__body text-gray-600 dark:text-gray-300">
          Watch how the coverage of the titles you're interested in grows over time. Get a look back at the journey that got you where you are today. Access in
          any media deck detail, check the stats of it for the full chart with milestones.
        </p>
      </div>

      <div class="jp-tile sm:col-span-2 border border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900">
        <div class="jp-tile__head">
          <Icon name="material-symbols:trending-up-rounded" class="jp-tile__icon" />
          <h3 class="jp-tile__title text-gray-900 dark:text-white">Your limits</h3>
        </div>
        <div class="mt-3 grid gap-x-6 gap-y-1.5 sm:grid-cols-2 text-sm">
          <div v-for="row in limitRows" :key="row.label" class="flex items-baseline justify-between gap-3 border-b border-gray-100 dark:border-gray-800 pb-1.5">
            <span class="text-gray-700 dark:text-gray-300">{{ row.label }}</span>
            <span class="tabular-nums font-semibold text-primary-600 dark:text-primary-300">{{ row.value }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
  .jp-tile {
    display: flex;
    flex-direction: column;
    border-radius: var(--radius-xl);
    padding: 1.1rem;
  }

  .jp-tile__head {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .jp-tile__icon {
    font-size: 1.3rem;
    color: var(--p-primary-500);
    flex-shrink: 0;
  }

  .jp-tile__title {
    font-weight: 600;
  }

  .jp-tile__body {
    margin-top: 0.5rem;
    font-size: 0.85rem;
    line-height: 1.5;
  }

  .jp-tile__actions {
    margin-top: auto;
    padding-top: 0.9rem;
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }
</style>
