<script setup lang="ts">
  const props = withDefaults(defineProps<{
    text?: string;
    wordId?: number;
    readingIndex?: number;
    sentenceId?: number;
    customSentenceId?: number;
    size?: 'sm' | 'md' | 'lg';
    type?: TtsType;
  }>(), { size: 'sm', type: 'word' });

  const iconSizeClass = computed(() => (props.size === 'sm' ? 'text-sm' : props.size === 'lg' ? '!text-3xl' : 'text-base'));

  const textRef = computed(() => props.text ?? '');
  const { speakWord, speakSentence, speakCustomSentence, speak, isSpeaking, isSupported, isLoading } = useTts(textRef, props.type);

  function handleClick() {
    if (props.customSentenceId) {
      speakCustomSentence(props.customSentenceId, props.text);
    } else if (props.sentenceId) {
      speakSentence(props.sentenceId, props.text);
    } else if (props.wordId !== undefined && props.readingIndex !== undefined) {
      speakWord(props.wordId, props.readingIndex, props.text);
    } else if (props.text) {
      speak(props.text);
    }
  }
</script>

<template>
  <button
    v-if="isSupported"
    type="button"
    class="inline-flex items-center justify-center text-surface-400 hover:text-primary-500 transition-colors cursor-pointer"
    :class="{ '!text-primary-500': isSpeaking }"
    title="Play audio"
    @click.stop="handleClick"
  >
    <i v-if="isLoading" class="pi pi-spin pi-spinner" :class="iconSizeClass" />
    <i v-else class="pi pi-volume-up" :class="iconSizeClass" />
  </button>
</template>
