<script setup lang="ts">
  // targetKnown: the sentence's own word is known too, so it adds nothing to the count.
  const props = withDefaults(defineProps<{ unknown?: number; targetKnown?: boolean }>(), { unknown: 0, targetKnown: false });

  const label = computed(() => `i+${props.unknown + (props.targetKnown ? 0 : 1)}`);
  const tooltip = computed(() =>
    props.unknown === 0
      ? props.targetKnown
        ? 'You already know every word in this sentence. Ignores names, particles & auxiliaries.'
        : 'You already know every other word in this sentence. Ignores names, particles & auxiliaries.'
      : `${props.unknown} other ${props.unknown === 1 ? 'word' : 'words'} in this sentence ${props.unknown === 1 ? 'is' : 'are'} new to you. Ignores names, particles & auxiliaries.`
  );
</script>

<template>
  <Tooltip :content="tooltip">
    <span
      class="text-[0.7rem] font-semibold leading-none tabular-nums cursor-help select-none"
      :class="unknown === 0 ? 'text-green-700 dark:text-green-400' : 'text-surface-500 dark:text-surface-400'"
      :aria-label="`${label}: ${tooltip}`"
    >
      {{ label }}
    </span>
  </Tooltip>
</template>
