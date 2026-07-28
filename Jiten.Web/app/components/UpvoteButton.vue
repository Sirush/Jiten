<script setup lang="ts">

const BOOST_WEIGHT = 5;

const props = withDefaults(
  defineProps<{
    hasUpvoted: boolean;
    upvoteCount: number;
    boostCount?: number;
    compact?: boolean;
  }>(),
  {
    boostCount: 0,
    compact: false,
  },
);

defineEmits<{
  toggle: [];
}>();

const score = computed(() => props.upvoteCount + props.boostCount * BOOST_WEIGHT);

const scoreTooltip = computed(() =>
  props.boostCount > 0
    ? `${score.value} votes — ${props.upvoteCount} upvote${props.upvoteCount === 1 ? '' : 's'} + ${props.boostCount} boost${props.boostCount === 1 ? '' : 's'} (+${props.boostCount * BOOST_WEIGHT})`
    : `${score.value} vote${score.value === 1 ? '' : 's'}`,
);
</script>

<template>
  <div v-if="compact" class="flex flex-col items-center gap-1 shrink-0">
    <Button
      icon="pi pi-chevron-up"
      :severity="hasUpvoted ? 'primary' : 'secondary'"
      :outlined="!hasUpvoted"
      size="small"
      rounded
      v-tooltip.top="hasUpvoted ? 'Remove upvote' : 'Upvote'"
      @click="$emit('toggle')"
    />
    <span v-tooltip.top="scoreTooltip" class="text-sm font-semibold">{{ score }}</span>
  </div>
  <Button
    v-else
    icon="pi pi-chevron-up"
    :label="String(score)"
    :severity="hasUpvoted ? 'primary' : 'secondary'"
    :outlined="!hasUpvoted"
    v-tooltip.top="scoreTooltip"
    @click="$emit('toggle')"
  />
</template>
