<script setup lang="ts">
  import type { SiteStatsKnownWords } from '~/types';
  import { floorToHundred, rankLabel } from '~/utils/siteStats';

  const props = defineProps<{
    knownWords: SiteStatsKnownWords;
  }>();

  const bars = computed(() => props.knownWords.atLeast.filter((b) => b.words > 0));
</script>

<template>
  <StatsSection v-if="bars.length && knownWords.learners > 0" title="How many words learners know">
    <p class="m-0">
      The median learner knows
      <b class="tabular-nums text-primary-700 dark:text-primary-300">{{ Math.round(knownWords.median).toLocaleString('en-US') }}</b> words.
      <template v-if="knownWords.p75 >= 100">
        One in four knows over <b class="tabular-nums text-primary-700 dark:text-primary-300">{{ floorToHundred(knownWords.p75).toLocaleString('en-US') }}</b
        >.
      </template>
    </p>
    <div class="flex items-end gap-1 sm:gap-2 h-44 pt-5" role="list" aria-label="Share of learners who know at least each number of words">
      <div v-for="bar in bars" :key="bar.words" class="relative flex-1 min-w-0 h-full flex items-end" role="listitem">
        <span
          class="absolute inset-x-0 text-center text-[11px] sm:text-xs tabular-nums text-gray-700 dark:text-gray-300"
          :style="{ bottom: `calc(${bar.share}% + 2px)` }"
          aria-hidden="true"
          >{{ Math.round(bar.share) }}%</span
        >
        <div class="w-full rounded-t-sm bg-primary-500 dark:bg-primary-400" :style="{ height: `${Math.max(bar.share, 0.5)}%` }" />
        <span class="sr-only">{{ Math.round(bar.share) }}% know at least {{ bar.words.toLocaleString('en-US') }} words</span>
      </div>
    </div>
    <div class="flex gap-1 sm:gap-2 -mt-2" aria-hidden="true">
      <span v-for="bar in bars" :key="bar.words" class="flex-1 min-w-0 text-center text-[11px] sm:text-xs text-gray-500 dark:text-gray-400"
        >{{ rankLabel(bar.words) }}+</span
      >
    </div>
  </StatsSection>
</template>
