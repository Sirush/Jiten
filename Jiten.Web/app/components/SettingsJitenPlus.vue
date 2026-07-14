<script setup lang="ts">
  import RedeemPromoCode from '~/components/RedeemPromoCode.vue';

  interface PromoCreditInfo {
    userPromoCreditId: number;
    remainingDays: number;
    grantsFullTier: boolean;
    grantedAt: string;
    thankYouMessage: string | null;
  }

  interface JitenPlusStatus {
    tier: 'none' | 'trial' | 'full';
    sources: {
      subscriptionActive: boolean;
      plan: string | null;
      periodEnd: string | null;
      isLifetime: boolean;
      lifetimeSource: string | null;
      promoCreditDays: number;
      credits: PromoCreditInfo[];
      adminOverride: boolean;
    };
  }

  const { $api } = useNuxtApp();

  const status = ref<JitenPlusStatus | null>(null);
  const loading = ref(true);

  async function loadStatus() {
    loading.value = true;
    try {
      status.value = await $api<JitenPlusStatus>('/jiten-plus/status');
    } catch {
      status.value = null;
    } finally {
      loading.value = false;
    }
  }

  const tier = computed(() => status.value?.tier ?? 'none');

  const tierLabel = computed(() => {
    if (tier.value === 'full') return 'Jiten+ Full';
    if (tier.value === 'trial') return 'Jiten+ Trial';
    return 'No active Jiten+';
  });

  const tierSeverity = computed(() => {
    if (tier.value === 'full') return 'success';
    if (tier.value === 'trial') return 'info';
    return 'secondary';
  });

  const detailLine = computed(() => {
    const s = status.value?.sources;
    if (!s) return '';
    if (s.isLifetime) return 'Lifetime access — never expires.';
    if (s.subscriptionActive) {
      const plan = s.plan ? s.plan.toLowerCase() : 'active';
      return `Active ${plan} subscription.`;
    }
    if (s.adminOverride) return 'Access granted by an administrator.';
    if (s.promoCreditDays > 0) {
      return `${s.promoCreditDays} day${s.promoCreditDays === 1 ? '' : 's'} left.`;
    }
    return 'Subscribe or redeem a code to unlock Jiten+ features.';
  });

  // Credit is genuinely bonus time: it only counts down when there's no paid plan running.
  const pausedNote = computed(() => {
    const s = status.value?.sources;
    if (!s) return null;
    if ((s.subscriptionActive || s.isLifetime) && s.promoCreditDays > 0) {
      return `Your ${s.promoCreditDays} Jiten+ credit day${s.promoCreditDays === 1 ? '' : 's'} are paused while your subscription is active.`;
    }
    return null;
  });

  const trialNote = computed(() =>
    tier.value === 'trial' ? 'Includes everything except the features that permanently store your data.' : null,
  );

  const thankYouMessages = computed(() =>
    (status.value?.sources.credits ?? [])
      .filter(c => !!c.thankYouMessage?.trim())
      .map(c => ({ id: c.userPromoCreditId, html: parseCustomMeaningHtml(c.thankYouMessage!) })),
  );

  onMounted(loadStatus);
</script>

<template>
  <Card>
    <template #title>
      <div class="flex items-center gap-2">
        <h3 class="text-lg font-semibold">Jiten+</h3>
        <Tag v-if="!loading" :value="tierLabel" :severity="tierSeverity" />
      </div>
    </template>
    <template #content>
      <div v-if="loading" class="text-gray-500 dark:text-gray-400">Loading…</div>
      <div v-else>
        <p class="text-gray-700 dark:text-gray-300">{{ detailLine }}</p>
        <p v-if="trialNote" class="text-sm text-gray-500 dark:text-gray-400 mt-1">{{ trialNote }}</p>
        <Message v-if="pausedNote" severity="info" :closable="false" class="mt-3">{{ pausedNote }}</Message>

        <div
          v-for="msg in thankYouMessages"
          :key="msg.id"
          class="mt-3 border-l-4 border-primary-500 pl-3 pr-2 py-2 bg-primary-50 dark:bg-primary-950/40 rounded-r"
        >
          <span class="text-xs tracking-wide text-primary-600 dark:text-primary-400 font-semibold">A note for you</span>
          <!-- eslint-disable-next-line vue/no-v-html -->
          <div class="break-words text-sm mt-0.5 text-gray-700 dark:text-gray-300" v-html="msg.html" />
        </div>

        <div class="mt-4 border-t border-surface-200 dark:border-surface-700 pt-4">
          <RedeemPromoCode @redeemed="loadStatus" />
        </div>
      </div>
    </template>
  </Card>
</template>
