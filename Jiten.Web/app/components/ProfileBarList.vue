<script setup lang="ts">
  import type { ProfileBar } from '~/types';

  defineProps<{ title: string; bars: ProfileBar[]; caption?: string }>();

  const headingId = useId();
</script>

<template>
  <section :aria-labelledby="headingId" class="flex flex-col gap-2">
    <h3 :id="headingId" class="text-sm font-semibold">{{ title }}</h3>
    <ul class="flex flex-col gap-1.5">
      <li v-for="bar in bars" :key="bar.key">
        <Tooltip :content="bar.tooltip ?? bar.value" block>
          <div
            tabindex="0"
            class="grid grid-cols-[minmax(0,8rem)_minmax(0,1fr)_auto] items-center gap-3 rounded text-sm focus-visible:outline-2 focus-visible:outline-primary-500"
          >
            <span class="truncate">{{ bar.label }}</span>
            <div class="h-2 overflow-hidden rounded bg-surface-200 dark:bg-surface-700" aria-hidden="true">
              <div
                class="h-full rounded"
                :class="bar.swatch ?? (bar.fraction >= 1 ? 'bg-primary-500 dark:bg-primary-400' : 'bg-primary-300 dark:bg-primary-400/40')"
                :style="{ width: `${Math.max(bar.fraction * 100, 1)}%` }"
              />
            </div>
            <span class="tabular-nums text-muted-color">{{ bar.value }}</span>
          </div>
        </Tooltip>
      </li>
    </ul>
    <p v-if="caption" class="text-xs text-muted-color">{{ caption }}</p>
  </section>
</template>
