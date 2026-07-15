<script setup lang="ts">
  const props = withDefaults(
    defineProps<{
      feature: string;
      tier?: 'any' | 'full';
    }>(),
    {
      tier: 'any',
    },
  );

  const { tierSatisfies, isTrial } = useJitenPlus();

  const required = computed<'trial' | 'full'>(() => (props.tier === 'full' ? 'full' : 'trial'));
  const granted = computed(() => tierSatisfies(required.value));

  // A Trial user hitting a Full-only (permanent-storage) feature gets the distinct chip + copy.
  const showFullChip = computed(() => props.tier === 'full' && isTrial.value);

  const tooltip = computed(() =>
    showFullChip.value
      ? 'Not part of the trial as this feature permanently stores your data. Unlocks with any paid plan, and your data stays even if you cancel later.'
      : 'Unlock with Jiten+',
  );
</script>

<template>
  <slot v-if="granted" />
  <div v-else class="jiten-plus-gate" :data-feature="feature">
    <div class="jiten-plus-gate__content" aria-hidden="true" inert>
      <slot />
    </div>
    <div class="jiten-plus-gate__overlay">
      <JitenPlusBadge v-tooltip.top="tooltip" :tier="showFullChip ? 'full' : 'any'" />
    </div>
  </div>
</template>

<style scoped>
  .jiten-plus-gate {
    position: relative;
  }

  .jiten-plus-gate__content {
    filter: blur(2px);
    opacity: 0.55;
    pointer-events: none;
    user-select: none;
  }

  .jiten-plus-gate__overlay {
    position: absolute;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1;
  }
</style>
