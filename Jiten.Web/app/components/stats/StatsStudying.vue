<script setup lang="ts">
  import type { SiteStatsStudiedDeck } from '~/types';
  import { coverUrl } from '~/utils/coverImage';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';

  defineProps<{
    decks: SiteStatsStudiedDeck[];
  }>();

  const localiseTitle = useLocaliseTitle();
</script>

<template>
  <StatsSection v-if="decks.length" title="What people are studying" subtitle="Titles learners added to their reviews in the last 90 days">
    <ol class="list-none m-0 p-0 grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1">
      <li v-for="(deck, index) in decks" :key="deck.deckId" class="min-w-0">
        <NuxtLink
          :to="`/decks/media/${deck.deckId}/detail`"
          target="_blank"
          class="grid items-center gap-3 rounded-md px-1.5 py-1.5 !no-underline text-inherit hover:bg-surface-100 dark:hover:bg-surface-800"
          style="grid-template-columns: 1.25rem 2.5rem minmax(0, 1fr) auto"
        >
          <span class="text-right text-sm tabular-nums text-gray-500 dark:text-gray-400">{{ index + 1 }}</span>
          <img :src="coverUrl(deck.coverName, 'xs')" alt="" width="40" height="60" loading="lazy" class="w-10 h-15 object-cover rounded-sm bg-surface-200 dark:bg-surface-800" />
          <span class="min-w-0">
            <span class="block truncate font-medium text-primary-700 dark:text-primary-300" v-bind="japaneseTextAttrs(localiseTitle(deck))">{{ localiseTitle(deck) }}</span>
            <span class="block text-xs text-gray-500 dark:text-gray-400">{{ getMediaTypeText(deck.mediaType) }}</span>
          </span>
          <span class="text-right leading-tight">
            <span class="block text-sm font-bold tabular-nums">{{ deck.learners.toLocaleString('en-US') }}</span>
            <span class="block text-xs text-gray-500 dark:text-gray-400">learners</span>
          </span>
        </NuxtLink>
      </li>
    </ol>
  </StatsSection>
</template>
