<script setup lang="ts">
  import type { ProfileFavouriteWord } from '~/types';

  defineProps<{
    word: Pick<ProfileFavouriteWord, 'text' | 'reading' | 'meaning'>;
    meaningEnd?: boolean;
  }>();
</script>

<template>
  <span lang="ja" class="shrink-0 text-lg font-bold">{{ word.text }}</span>
  <span v-if="word.reading" lang="ja" class="shrink-0 text-xs text-muted-color">{{ word.reading }}</span>
  <span class="min-w-0 flex-1 truncate text-sm" :class="{ 'text-right': meaningEnd }">{{ word.meaning }}</span>
</template>
