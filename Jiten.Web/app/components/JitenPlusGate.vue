<script setup lang="ts">
  const props = withDefaults(
    defineProps<{
      feature: string;
      tier?: 'any' | 'full';
      // Human-readable name for the dialog
      featureLabel?: string;
      // Corner dot for small buttons
      compact?: boolean;
    }>(),
    {
      tier: 'any',
      featureLabel: undefined,
      compact: false,
    }
  );

  const { tierSatisfies, isTrial } = useJitenPlus();

  const required = computed<'trial' | 'full'>(() => (props.tier === 'full' ? 'full' : 'trial'));
  const granted = computed(() => tierSatisfies(required.value));

  // A Trial user hitting a Full-only (permanent-storage) feature gets the distinct chip + copy.
  const showFullChip = computed(() => props.tier === 'full' && isTrial.value);

  const tooltip = computed(() =>
    showFullChip.value
      ? 'Not part of the trial as this feature permanently stores your data. Unlocks with any paid plan, and your data stays even if you cancel later.'
      : 'Unlock with Jiten+'
  );

  const showUpsell = ref(false);

  function goToJitenPlus() {
    showUpsell.value = false;
    navigateTo('/jiten-plus');
  }
</script>

<template>
  <slot v-if="granted" />
  <template v-else>
    <div
      v-if="compact"
      v-tooltip.top="tooltip"
      class="jiten-plus-gate jiten-plus-gate--compact"
      :data-feature="feature"
      role="button"
      tabindex="0"
      :aria-label="`${featureLabel ?? 'This feature'} requires Jiten+`"
      @click="showUpsell = true"
      @keydown.enter.prevent="showUpsell = true"
      @keydown.space.prevent="showUpsell = true"
    >
      <div class="jiten-plus-gate__content jiten-plus-gate__content--compact" aria-hidden="true" inert>
        <slot />
      </div>
      <span class="jiten-plus-gate__dot" aria-hidden="true">
        <i class="pi pi-lock" />
      </span>
    </div>
    <div v-else class="jiten-plus-gate" :data-feature="feature">
      <div class="jiten-plus-gate__content" aria-hidden="true" inert>
        <slot />
      </div>
      <div
        v-tooltip.top="tooltip"
        class="jiten-plus-gate__overlay"
        role="button"
        tabindex="0"
        :aria-label="`${featureLabel ?? 'This feature'} requires Jiten+`"
        @click="showUpsell = true"
        @keydown.enter.prevent="showUpsell = true"
        @keydown.space.prevent="showUpsell = true"
      >
        <JitenPlusBadge :link="false" :tier="showFullChip ? 'full' : 'any'" />
      </div>
    </div>

    <Dialog v-model:visible="showUpsell" modal :style="{ width: '400px' }" :breakpoints="{ '480px': '92vw' }">
      <template #header>
        <div class="flex items-center gap-2">
          <JitenPlusBadge :link="false" :tier="showFullChip ? 'full' : 'any'" />
          <span class="font-semibold">{{ featureLabel ?? 'Jiten+ feature' }}</span>
        </div>
      </template>
      <div class="flex flex-col gap-2 text-sm">
        <p>
          <span class="font-semibold">{{ featureLabel ?? 'This feature' }}</span>
          is part of Jiten+.
        </p>
        <p v-if="showFullChip">
          It isn't included in the trial because it permanently stores your data. It unlocks with any paid plan, and your data stays even if you cancel later.
        </p>
        <p v-else class="text-muted-color">Jiten+ unlocks useful extras while helping support Jiten.</p>
      </div>
      <template #footer>
        <Button label="Maybe later" severity="secondary" text @click="showUpsell = false" />
        <Button label="See Jiten+" icon="pi pi-sparkles" @click="goToJitenPlus" />
      </template>
    </Dialog>
  </template>
</template>

<style scoped>
  .jiten-plus-gate {
    position: relative;
  }

  .jiten-plus-gate--compact {
    display: inline-flex;
    cursor: pointer;
  }

  .jiten-plus-gate__content {
    filter: blur(1px) saturate(0);
    pointer-events: none;
    user-select: none;
  }

  .jiten-plus-gate__content--compact {
    filter: grayscale(1);
    opacity: 0.65;
  }

  .jiten-plus-gate__overlay {
    position: absolute;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1;
    cursor: pointer;
  }

  .jiten-plus-gate__overlay:hover .jiten-plus-badge,
  .jiten-plus-gate__overlay:focus-visible .jiten-plus-badge {
    background: var(--p-primary-200);
  }

  :global(.dark-mode .jiten-plus-gate__overlay:hover .jiten-plus-badge),
  :global(.dark-mode .jiten-plus-gate__overlay:focus-visible .jiten-plus-badge) {
    background: var(--p-primary-800);
  }

  .jiten-plus-gate__overlay:focus-visible {
    outline: 2px solid var(--p-primary-500);
    outline-offset: -2px;
    border-radius: var(--radius-lg);
  }

  .jiten-plus-gate__dot {
    position: absolute;
    top: -0.3rem;
    right: -0.3rem;
    width: 1.05rem;
    height: 1.05rem;
    border-radius: 9999px;
    background: var(--p-primary-500);
    color: #fff;
    display: flex;
    align-items: center;
    justify-content: center;
    border: 1.5px solid var(--p-content-background);
    z-index: 1;
  }

  .jiten-plus-gate__dot .pi {
    font-size: 0.5rem;
  }

  .jiten-plus-gate--compact:hover .jiten-plus-gate__content--compact {
    opacity: 0.85;
  }

  .jiten-plus-gate--compact:focus-visible {
    outline: 2px solid var(--p-primary-500);
    outline-offset: 2px;
    border-radius: var(--radius-lg);
  }
</style>
