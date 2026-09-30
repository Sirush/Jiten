<script setup lang="ts">
  import type { WordSummary } from '~/types';

  defineProps<{
    components: WordSummary[];
    hideHeading?: boolean;
  }>();

  const convertToRuby = useConvertToRuby();
</script>

<template>
  <div v-if="components.length > 0" class="mt-2">
    <h3 v-if="!hideHeading" class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mb-2">Composed of</h3>
    <div class="flex flex-wrap items-center gap-2">
      <template v-for="(component, i) in components" :key="i">
        <span
          v-if="component.isGrammatical"
          class="inline-flex items-center px-1 py-2 text-xl text-surface-500 dark:text-surface-400"
          lang="ja"
        >
          {{ component.reading }}
        </span>
        <Tooltip v-else :content="component.mainDefinition ?? ''">
          <NuxtLink
            :to="`/vocabulary/${component.wordId}/${component.readingIndex}`"
            class="group relative inline-flex items-center gap-3 px-3 py-2 rounded-lg border border-surface-200 dark:border-surface-700 hover:border-primary-500 dark:hover:border-primary-400 hover:bg-surface-50 dark:hover:bg-surface-800 transition-all"
          >
            <span class="text-xl font-medium" lang="ja" v-html="convertToRuby(component.readingFurigana || component.reading)" />
            <span v-if="component.mainDefinition" class="text-surface-600 dark:text-surface-400 text-xs max-w-[14rem] line-clamp-2">
              {{ component.mainDefinition }}
            </span>
          </NuxtLink>
        </Tooltip>
      </template>
    </div>
  </div>
</template>
