<script setup lang="ts">
  import type { CardImageBlockOptions, CardLayoutBlock } from '~/types';
  import { cardImageDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<CardImageBlockOptions>(cardImageDefaults, props.block.options));

  const { cardImage, cardImageUrl, imageBlurred, imageBesideLayout, hasCardMedia, canEditCardMedia, openMediaEditor, onImageError, revealImage } =
    useCardContext();

  const showEditButton = computed(() => opts.value.showEditButton && canEditCardMedia.value);
</script>

<template>
  <!-- Below-style rendering owned by this block at its stack position. For beside layout the md+ image
       lives in the headword grid, so here it is only the mobile fallback (md:hidden). -->
  <div v-if="cardImage" class="my-2 flex w-full justify-center" :class="{ 'md:hidden': imageBesideLayout }" @click.stop>
    <SrsCardImage
      :url="cardImageUrl"
      :blurred="imageBlurred"
      img-class="max-h-[40vh] w-auto rounded-lg object-contain"
      @error="onImageError"
      @reveal="revealImage"
    />
  </div>

  <div v-if="showEditButton" class="my-2 flex w-full justify-center" @click.stop>
    <button
      type="button"
      class="inline-flex items-center gap-1.5 text-xs text-surface-400 hover:text-primary-500 transition-colors cursor-pointer"
      @click="openMediaEditor"
    >
      <i class="pi pi-image text-sm" />
      {{ hasCardMedia ? 'Edit card media' : 'Add image or audio' }}
    </button>
  </div>
</template>
