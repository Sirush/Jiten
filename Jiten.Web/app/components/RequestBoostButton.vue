<script setup lang="ts">
  import type { BoostBalance } from '~/composables/useMediaRequests';

  const props = withDefaults(
    defineProps<{
      requestId: number;
      boostCount: number;
      hasBoosted: boolean;
      boostable: boolean;
      compact?: boolean;
      // When a parent manages the balance (e.g. the list page fetches it once and shares it),
      // pass it here and set autoFetchBalance=false so each button doesn't refetch.
      balance?: BoostBalance | null;
      autoFetchBalance?: boolean;
    }>(),
    {
      compact: false,
      balance: null,
      autoFetchBalance: true,
    }
  );

  const emit = defineEmits<{
    boosted: [payload: { boostCount: number; balance: BoostBalance }];
  }>();

  const { boostRequest, fetchBoostBalance, error: apiError } = useMediaRequests();
  const { isPlus, hasFeature } = useJitenPlus();
  const toast = useToast();

  const canBoost = computed(() => hasFeature('request-boosts'));

  const internalBalance = ref<BoostBalance | null>(null);
  const balance = computed(() => (props.autoFetchBalance ? internalBalance.value : props.balance));
  const showConfirm = ref(false);
  const isBoosting = ref(false);

  async function loadBalance() {
    if (!props.autoFetchBalance || !isPlus.value) return;
    internalBalance.value = await fetchBoostBalance();
  }

  onMounted(loadBalance);
  // The balance only becomes fetchable once the tier status resolves (client-only, async).
  watch(isPlus, (val) => {
    if (val && !internalBalance.value) loadBalance();
  });

  const resetDate = computed(() => {
    if (!balance.value) return '';
    return new Date(balance.value.resetAt).toLocaleDateString('en-US', { month: 'long', day: 'numeric' });
  });

  const exhausted = computed(() => balance.value !== null && balance.value.remaining <= 0);

  const disabled = computed(() => props.hasBoosted || exhausted.value);

  const tooltip = computed(() => {
    if (props.hasBoosted) return 'You have already boosted this request.';
    if (exhausted.value) return `No boosts left. Your allowance resets on ${resetDate.value} (UTC).`;
    return 'Boost';
  });

  const buttonLabel = computed(() => (props.hasBoosted ? 'Boosted' : 'Boost'));

  async function confirmBoost() {
    isBoosting.value = true;
    const result = await boostRequest(props.requestId);
    isBoosting.value = false;
    if (result) {
      if (props.autoFetchBalance) internalBalance.value = result.balance;
      emit('boosted', { boostCount: result.boostCount, balance: result.balance });
      showConfirm.value = false;
      toast.add({
        severity: 'success',
        summary: 'Request boosted',
        detail: `${result.balance.remaining} of ${result.balance.limit} boosts left this month.`,
        life: 4000,
      });
    } else {
      // Refresh balance so an out-of-sync client (e.g. already boosted elsewhere) recovers.
      await loadBalance();
      showConfirm.value = false;
      const detail = extractApiError(apiError.value, 'Failed to boost this request. Please try again.');
      toast.add({ severity: 'error', summary: 'Boost failed', detail, life: 6000 });
    }
  }
</script>

<template>
  <!-- Compact (list card) form: mirrors UpvoteButton's vertical layout. -->
  <div v-if="compact && boostable" class="flex flex-col items-center gap-1 shrink-0">
    <Button
      v-if="canBoost"
      icon="pi pi-bolt"
      v-tooltip.top="tooltip"
      :severity="hasBoosted ? 'secondary' : 'help'"
      :outlined="!hasBoosted"
      size="small"
      rounded
      :disabled="disabled"
      @click="showConfirm = true"
    />
    <JitenPlusGate v-else feature="request-boosts" feature-label="Request boosts" compact>
      <Button icon="pi pi-bolt" severity="help" outlined size="small" rounded />
    </JitenPlusGate>
  </div>

  <!-- Full form (label + inline balance). -->
  <div v-else-if="!compact" class="flex flex-col gap-1">
    <div class="flex items-center gap-2">
      <!-- Boost count is always visible, kept separate from votes so signals stay honest. -->
      <span
        v-if="boostCount > 0"
        v-tooltip.top="`${boostCount} boost${boostCount === 1 ? '' : 's'} (+${boostCount * 5} votes)`"
        class="inline-flex items-center gap-1 text-sm font-semibold text-amber-600 dark:text-amber-400"
      >
        <i class="pi pi-bolt text-xs" />
        {{ boostCount }}
      </span>

      <template v-if="boostable">
        <Button
          v-if="canBoost"
          :label="buttonLabel"
          icon="pi pi-bolt"
          v-tooltip.top="tooltip"
          :severity="hasBoosted ? 'secondary' : 'help'"
          :outlined="!hasBoosted"
          :disabled="disabled"
          @click="showConfirm = true"
        />
        <JitenPlusGate v-else feature="request-boosts" feature-label="Request boosts" compact>
          <Button label="Boost" icon="pi pi-bolt" severity="help" outlined />
        </JitenPlusGate>
      </template>
    </div>
  </div>

  <Dialog v-model:visible="showConfirm" header="Boost this request?" :modal="true" :style="{ width: '440px' }" :breakpoints="{ '480px': '92vw' }">
    <div class="flex flex-col gap-2 text-sm">
      <p>Boosting permanently adds <span class="font-semibold">+5 votes</span> to this request, making it more likely to be fulfilled earlier.</p>
      <p>
        This uses <span class="font-semibold">one of your {{ balance?.limit ?? 5 }} monthly boosts</span> and
        <span class="font-semibold">cannot be undone</span>.
      </p>
      <p v-if="balance" class="text-muted-color">You have {{ balance.remaining }} boost{{ balance.remaining === 1 ? '' : 's' }} left this month.</p>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text :disabled="isBoosting" @click="showConfirm = false" />
      <Button label="Boost" icon="pi pi-bolt" severity="help" :loading="isBoosting" @click="confirmBoost" />
    </template>
  </Dialog>
</template>
