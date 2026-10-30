<script setup lang="ts">
  import type { MediaSuggestion } from '~/types';
  import { coverUrl } from '~/utils/coverImage';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';

  defineProps<{ deck: MediaSuggestion }>();

  const localiseTitle = useLocaliseTitle();
</script>

<template>
  <img :src="coverUrl(deck.coverName, 'xs')" alt="" loading="lazy" class="h-12 w-8 shrink-0 rounded object-cover" />
  <span class="flex min-w-0 flex-1 flex-col">
    <span class="truncate text-sm font-medium" v-bind="japaneseTextAttrs(localiseTitle(deck))">{{ localiseTitle(deck) }}</span>
    <span class="text-xs text-muted-color">{{ getMediaTypeText(deck.mediaType) }}</span>
  </span>
</template>
