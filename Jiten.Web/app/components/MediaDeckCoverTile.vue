<script setup lang="ts">
  import type { DeckCard } from '~/types';

  defineProps<{
    deck: DeckCard;
    badge?: string;
    badgeTitle?: string;
  }>();

  const localiseTitle = useLocaliseTitle();
</script>

<template>
  <div class="flex w-34 flex-col items-center gap-1">
    <div class="relative">
      <span
        v-if="badge"
        class="absolute top-1 left-1 z-10 rounded bg-primary px-1.5 py-0.5 text-xs font-bold tabular-nums text-primary-contrast shadow"
        :title="badgeTitle"
      >
        {{ badge }}
      </span>
      <MediaDeckCompactView :deck="deck" lazy-cover />
    </div>
    <NuxtLink
      :to="`/decks/media/${deck.deckId}/detail`"
      class="line-clamp-2 w-full text-center text-sm hover:underline"
      :title="localiseTitle(deck)"
      v-bind="japaneseTextAttrs(localiseTitle(deck))"
    >
      {{ localiseTitle(deck) }}
    </NuxtLink>
    <slot />
  </div>
</template>
