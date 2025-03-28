<script setup lang="ts">
  import type { Deck } from '~/types';
  import Card from 'primevue/card';
  import {getChildrenCountText, getMediaTypeText} from '~/utils/mediaTypeMapper';
  import { getLinkTypeText } from '~/utils/linkTypeMapper';

  const props = defineProps<{
    deck: Deck;
    isCompact?: bool;
  }>();

  const showDownloadDialog = ref(false);
</script>

<template>
  <Card class="p-2">
    <template #title>{{ localiseTitle(deck) }}</template>
    <template #subtitle v-if="!isCompact">{{ getMediaTypeText(deck.mediaType) }}</template>
    <template #content>
      <div class="flex-gap-6">
        <div class="flex-1">
          <div class="flex flex-col md:flex-row gap-x-8 gap-y-2">
            <div v-if="!isCompact">
              <img
                :src="deck.coverName == 'nocover.jpg' ? '/img/nocover.jpg' : deck.coverName"
                :alt="deck.originalTitle"
                class="h-48 w-34"
              />
            </div>
            <div>
              <div class="flex flex-col gap-x-8 gap-y-2" :class="isCompact ? '' : 'md:flex-row'">
                <div class="w-full md:w-64">
                  <div class="flex justify-between mb-2">
                    <span class="text-gray-600 dark:text-gray-300">Character count</span>
                    <span class="ml-8 tabular-nums">{{ deck.characterCount.toLocaleString() }}</span>
                  </div>
                  <div class="flex justify-between mb-2">
                    <span class="text-gray-600 dark:text-gray-300">Word count</span>
                    <span class="ml-8 tabular-nums">{{ deck.wordCount.toLocaleString() }}</span>
                  </div>
                  <div class="flex justify-between mb-2">
                    <span class="text-gray-600 dark:text-gray-300">Unique words</span>
                    <span class="ml-8 tabular-nums">{{ deck.uniqueWordCount.toLocaleString() }}</span>
                  </div>
                  <div class="flex justify-between mb-2">
                    <span class="text-gray-600 dark:text-gray-300">Unique words used once</span>
                    <span class="ml-8 tabular-nums">{{ deck.uniqueWordUsedOnceCount.toLocaleString() }}</span>
                  </div>
                </div>

                <div class="w-full md:w-64">
                  <div class="flex justify-between mb-2">
                    <span class="text-gray-600 dark:text-gray-300">Unique kanji</span>
                    <span class="ml-8 tabular-nums">{{ deck.uniqueKanjiCount.toLocaleString() }}</span>
                  </div>
                  <div class="flex justify-between mb-2">
                    <span class="text-gray-600 dark:text-gray-300">Unique kanji used once</span>
                    <span class="ml-8 tabular-nums">{{ deck.uniqueKanjiUsedOnceCount.toLocaleString() }}</span>
                  </div>
                  <div v-if="deck.averageSentenceLength !== 0" class="flex justify-between mb-2">
                    <span class="text-gray-600 dark:text-gray-300">Average sentence length</span>
                    <span class="ml-8 tabular-nums">{{ deck.averageSentenceLength.toFixed(1) }}</span>
                  </div>
                  <div v-if="deck.difficulty != 0" class="flex justify-between mb-2">
                    <span
                      v-tooltip="
                        'This is a work in progress.\nThe current analysis only takes into account the vocabulary and not the grammar patterns, cultural references or wordplay, which might make some works easier or harder than the score they\'re given.'
                      "
                      class="text-gray-600 dark:text-gray-300"
                    >
                      Difficulty
                      <span class="text-purple-500 text-xs align-super"> beta </span>
                    </span>
                    <span class="ml-8 tabular-nums">{{ deck.difficulty }}</span>
                  </div>
                </div>

                <div class="w-full md:w-64">
                  <div v-if="deck.childrenDeckCount != 0" class="flex justify-between mb-2">
                    <span class="text-gray-600 dark:text-gray-300">{{ getChildrenCountText(deck.mediaType) }}</span>
                    <span class="ml-8 tabular-nums">{{ deck.childrenDeckCount.toLocaleString() }}</span>
                  </div>
                </div>
              </div>

              <div class="mt-4 flex flex-col md:flex-row gap-4">
                <a v-for="link in deck.links" :href="link.url" target="_blank">{{ getLinkTypeText(link.linkType) }}</a>
              </div>
              <div class="mt-4">
                <div class="flex flex-col md:flex-row gap-4">
                  <Button as="router-link" :to="`/decks/media/${deck.deckId}/detail`" label="View details" class="" />
                  <Button
                    as="router-link"
                    :to="`/decks/media/${deck.deckId}/vocabulary`"
                    label="View vocabulary"
                    class=""
                  />
                  <Button @click="showDownloadDialog = true" label="Download deck" class="" />
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </template>
  </Card>

  <MediaDeckDownloadDialog :deck="deck" :visible="showDownloadDialog" @update:visible="showDownloadDialog = $event" />
</template>

<style scoped></style>
