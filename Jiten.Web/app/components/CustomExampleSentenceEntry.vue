<script setup lang="ts">
  import type { UserExampleSentenceDto } from '~/types';

  const props = defineProps<{
    sentence: UserExampleSentenceDto;
  }>();

  const formattedText = computed(() => parseCustomSentenceHtml(props.sentence.text));
  const plainText = computed(() => props.sentence.text.replace(/\*\*/g, ''));
</script>

<template>
  <div class="flex flex-col">
    <blockquote class="relative inline-block border-l-4 border-yellow-500 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r shadow-sm overflow-hidden">
      <div class="flex items-start gap-2">
        <div class="md:text-lg text-sm flex-1" lang="ja" v-html="formattedText" />
        <TtsButton :text="plainText" :custom-sentence-id="sentence.userExampleSentenceId" type="sentence" size="sm" class="mt-0.5 shrink-0" />
      </div>
    </blockquote>
    <div v-if="sentence.source" class="flex items-center mb-2">
      <span class="text-xs italic mr-2 ml-4">Source:</span>
      <span class="text-xs">{{ sentence.source }}</span>
    </div>
  </div>
</template>
