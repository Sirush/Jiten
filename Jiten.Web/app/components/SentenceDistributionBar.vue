<script setup lang="ts">
  const props = defineProps<{
    readable: number;
    oneUnknown: number;
    twoUnknown: number;
    threeOrMoreUnknown: number;
  }>();

  const total = computed(() => props.readable + props.oneUnknown + props.twoUnknown + props.threeOrMoreUnknown);
  const percent = (count: number) => (total.value > 0 ? (count * 100) / total.value : 0);
  const whole = (count: number) => `${Math.floor(percent(count))}%`;

  const segments = computed(() => [
    { key: 'i0', label: 'i+0', count: props.readable, fill: 'bg-green-500' },
    { key: 'i1', label: 'i+1', count: props.oneUnknown, fill: 'bg-green-500/60' },
    { key: 'i2', label: 'i+2', count: props.twoUnknown, fill: 'bg-green-500/30' },
    { key: 'i3', label: 'i+3+', count: props.threeOrMoreUnknown, fill: 'bg-gray-200 dark:bg-gray-700' },
  ]);

  const visibleSegments = computed(() => segments.value.filter((s) => s.count > 0));

  const tooltip = computed(() => {
    const { readable, oneUnknown, twoUnknown } = props;
    return [
      `${whole(readable)} with every word known`,
      `${whole(readable + oneUnknown)} with at most one new word`,
      `${whole(readable + oneUnknown + twoUnknown)} with at most two`,
    ].join('\n');
  });
</script>

<template>
  <div class="flex flex-col gap-2">
    <Tooltip :content="tooltip" block>
      <div class="flex w-full h-3 gap-0.5 rounded-full overflow-hidden cursor-help" role="img" :aria-label="tooltip">
        <div
          v-for="segment in visibleSegments"
          :key="segment.key"
          :class="segment.fill"
          class="h-full transition-all duration-700"
          :style="{ width: percent(segment.count).toFixed(2) + '%' }"
        />
      </div>
    </Tooltip>

    <ul class="flex flex-wrap gap-x-5 gap-y-1 text-sm">
      <li v-for="segment in segments" :key="segment.key" class="flex items-center gap-1.5">
        <span class="inline-block w-2.5 h-2.5 rounded-full shrink-0" :class="segment.fill" aria-hidden="true" />
        <span class="text-gray-600 dark:text-gray-300">{{ segment.label }}</span>
        <span class="font-semibold tabular-nums">{{ whole(segment.count) }}</span>
      </li>
    </ul>

    <p class="text-xs text-gray-500 dark:text-gray-400">
      i+N is the number of words in a sentence you don't know yet. Names, particles and auxiliaries don't count.
    </p>
  </div>
</template>
