<script setup lang="ts">
  import type { DeckCard } from '~/types';
  import { coverUrl } from '~/utils/coverImage';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { formatScore } from '~/utils/formatScore';

  withDefaults(defineProps<{ deck: DeckCard; score?: number | null; showCoverage?: boolean }>(), { score: null, showCoverage: false });

  const localiseTitle = useLocaliseTitle();
</script>

<template>
  <div class="flex min-w-0 flex-col gap-1.5">
    <NuxtLink
      :to="`/decks/media/${deck.deckId}/detail`"
      class="group relative block aspect-[2/3] overflow-hidden rounded-md bg-surface-200 dark:bg-surface-800"
    >
      <img
        :src="coverUrl(deck.coverName, 'md')"
        :alt="localiseTitle(deck)"
        loading="lazy"
        class="h-full w-full object-cover transition-opacity group-hover:opacity-90"
      />
      <span
        v-if="score != null"
        class="absolute bottom-1.5 left-1.5 rounded bg-black/75 px-1.5 py-0.5 text-xs font-bold tabular-nums text-white"
        :aria-label="`Scored ${formatScore(score)} out of 10`"
      >
        {{ formatScore(score) }}
      </span>
    </NuxtLink>
    <NuxtLink
      :to="`/decks/media/${deck.deckId}/detail`"
      class="line-clamp-2 min-h-[2lh] text-sm font-medium leading-snug !text-surface-900 !no-underline hover:!text-primary-600 dark:!text-surface-0 dark:hover:!text-primary-300"
      :title="localiseTitle(deck)"
      v-bind="japaneseTextAttrs(localiseTitle(deck))"
    >
      {{ localiseTitle(deck) }}
    </NuxtLink>
    <span class="text-xs text-muted-color">{{ getMediaTypeText(deck.mediaType) }}</span>
    <slot />
    <ProfileViewerCoverage v-if="showCoverage" :deck="deck" stacked />
  </div>
</template>
