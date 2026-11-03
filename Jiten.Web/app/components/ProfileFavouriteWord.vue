<script setup lang="ts">
  import type { ProfileFavouriteWord } from '~/types';

  const props = defineProps<{
    word: Pick<ProfileFavouriteWord, 'text' | 'reading' | 'meaning'>;
  }>();

  const showReading = computed(() => !!props.word.reading && props.word.reading !== props.word.text);
</script>

<template>
  <div class="min-w-0 flex-1">
    <div class="flex flex-wrap items-baseline gap-x-2.5">
      <span lang="ja" class="line-clamp-2 text-lg/snug font-bold [overflow-wrap:anywhere] [word-break:auto-phrase]">{{ word.text }}</span>
      <span v-if="showReading" lang="ja" class="text-xs text-muted-color [overflow-wrap:anywhere]">{{ word.reading }}</span>
    </div>
    <p v-if="word.meaning" class="mt-0.5 line-clamp-1 text-sm text-muted-color group-focus-within:line-clamp-none group-hover:line-clamp-none">{{ word.meaning }}</p>
  </div>
</template>
