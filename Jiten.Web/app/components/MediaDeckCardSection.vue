<script setup lang="ts">
  import type { Deck } from '~/types';
  import type { MediaCardSectionId } from '~/utils/mediaCardSections';

  defineProps<{ section: MediaCardSectionId; deck: Deck }>();
  const descriptionExpanded = defineModel<boolean>('descriptionExpanded', { default: false });
</script>

<template>
  <div :data-section="section">
    <div v-if="section === 'description'" class="description-container" :class="{ expanded: descriptionExpanded }">
      <p class="whitespace-pre-line mb-0 text-sm leading-relaxed text-gray-600 dark:text-gray-400" v-bind="japaneseTextAttrs(deck.description)">{{ deck.description }}</p>
      <button
        v-if="(deck.description?.length ?? 0) > 50"
        type="button"
        class="text-primary-500 hover:text-primary-700 text-sm cursor-pointer"
        @click="descriptionExpanded = !descriptionExpanded"
      >
        {{ descriptionExpanded ? 'View less' : 'View more' }}
      </button>
    </div>
    <GenreTagDisplay v-else-if="section === 'genres'" :genres="deck.genres" label="Genres" />
    <GenreTagDisplay v-else-if="section === 'tags'" :tags="deck.tags" label="Tags" />
    <RelatedMediaDisplay
      v-else-if="section === 'relations'"
      :relationships="deck.relationships ?? []"
      :series="deck.series"
      :franchise-id="deck.franchiseId"
      :deck-id="deck.deckId"
    />
  </div>
</template>

<style scoped>
  .description-container:not(.expanded) p {
    display: -webkit-box;
    line-clamp: 2;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  @media (max-width: 768px) {
    .description-container:not(.expanded) p {
      line-clamp: 4;
      -webkit-line-clamp: 4;
    }
  }

  .description-container.expanded p {
    white-space: pre-line;
  }
</style>
