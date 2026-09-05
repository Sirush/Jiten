<script setup lang="ts">
  import { pickDescribeExamples } from '~/utils/describeExamples';

  const seed = useState('home-try-seed', () => Math.floor(Math.random() * 0xffffffff));
  const examples = computed(() => pickDescribeExamples(seed.value));
</script>

<template>
  <div class="flex items-center gap-1.5 overflow-x-auto sm:overflow-visible sm:flex-wrap sm:gap-y-1.5 [scrollbar-width:none] [&::-webkit-scrollbar]:hidden text-xs text-surface-500 dark:text-surface-400">
    <span class="shrink-0">Try:</span>
    <NuxtLink
      v-for="example in examples"
      :key="example"
      :to="{ path: '/decks/media', query: { describe: example } }"
      class="shrink-0 rounded-full border border-primary-200 dark:border-primary-800 px-2.5 py-0.5 !text-primary-700 dark:!text-primary-300 !no-underline hover:bg-primary-50 dark:hover:bg-primary-900/40 cursor-pointer whitespace-nowrap"
    >
      {{ example }}
    </NuxtLink>
  </div>
</template>

