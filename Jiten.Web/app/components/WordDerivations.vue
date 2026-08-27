<script setup lang="ts">
  import type { WordDerivationDto } from '~/types/types';

  const props = defineProps<{
    derivedFrom?: WordDerivationDto[];
    derives?: WordDerivationDto[];
  }>();

  const convertToRuby = useConvertToRuby();

  const sections = computed(() =>
    [
      { heading: 'Derived from', links: props.derivedFrom ?? [] },
      { heading: 'Derived forms', links: props.derives ?? [] },
    ].filter((section) => section.links.length > 0)
  );
</script>

<template>
  <div v-if="sections.length > 0" class="mt-2 flex flex-col gap-3">
    <div v-for="section in sections" :key="section.heading">
      <h3 class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mb-2">{{ section.heading }}</h3>
      <div class="flex flex-wrap gap-2">
        <NuxtLink
          v-for="link in section.links"
          :key="`${section.heading}-${link.wordId}-${link.readingIndex}-${link.categoryKey}`"
          :to="`/vocabulary/${link.wordId}/${link.readingIndex}`"
          class="group relative inline-flex items-center gap-3 px-3 py-2 rounded-lg border border-surface-200 dark:border-surface-700 hover:border-primary-500 dark:hover:border-primary-400 hover:bg-surface-50 dark:hover:bg-surface-800 transition-all"
        >
          <span class="text-xl font-medium" lang="ja" v-html="convertToRuby(link.rubyText || link.text)" />
          <span class="text-surface-600 dark:text-surface-400 text-xs max-w-[14rem] line-clamp-2">
            {{ link.categoryLabel }}
            <span v-if="link.enabled === false" class="text-surface-500 dark:text-surface-400">(not counted as known)</span>
          </span>
        </NuxtLink>
      </div>
    </div>
  </div>
</template>
