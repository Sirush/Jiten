<script setup lang="ts">
  import type { CardLayoutBlock } from '~/types';
  import { useCardContext } from './useCardContext';

  defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();

  const { cardImage, cardImageUrl, imageBlurred, imageBesideLayout, onImageError, revealImage } = useCardContext();
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
</template>
