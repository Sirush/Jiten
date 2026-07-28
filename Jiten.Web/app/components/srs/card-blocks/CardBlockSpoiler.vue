<script setup lang="ts">
  import { useCardContext } from './useCardContext';

  const props = defineProps<{ enabled: boolean }>();

  const { card, isPreview } = useCardContext();

  const revealed = ref(false);
  const cardKey = computed(() => (isPreview ? 'preview' : card.value ? `${card.value.wordId}-${card.value.readingIndex}` : ''));
  watch(cardKey, () => {
    revealed.value = false;
  });

  const blurred = computed(() => props.enabled && !revealed.value);

  // Capture phase so the reveal wins over any inner @click.stop (e.g. confusable readings) and, while
  // blurred, swallows the click so revealing the content does not also flip the card.
  function onCaptureClick(e: MouseEvent) {
    if (!blurred.value) return;
    e.stopPropagation();
    revealed.value = true;
  }
</script>

<template>
  <div :class="{ 'blur-md select-none cursor-pointer': blurred }" @click.capture="onCaptureClick">
    <slot />
  </div>
</template>
