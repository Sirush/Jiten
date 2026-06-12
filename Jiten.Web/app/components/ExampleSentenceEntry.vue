<script setup lang="ts">
  import type { ExampleSentence } from '~/types';
  import { computed, ref } from 'vue';

  const props = defineProps<{
    exampleSentence: ExampleSentence;
    showSource?: boolean;
    wordId?: number;
    readingIndex?: number;
  }>();

  const emit = defineEmits<{
    favourited: [];
  }>();

  import { useToast } from 'primevue/usetoast';

  const { $api } = useNuxtApp();
  const authStore = useAuthStore();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();
  const store = useJitenStore();
  const isNsfw = isTextNsfw(props.exampleSentence.text);
  const favourited = ref(false);
  const isRevealed = computed({
    get: () => store.displayAllNsfw,
    set: (value) => {
      store.displayAllNsfw = value;
    },
  });

  const formattedText = computed(() => {
    const { text, wordPosition, wordLength } = props.exampleSentence;
    if (wordPosition < 0 || wordLength <= 0 || wordPosition >= text.length) {
      return sanitiseHtml(text);
    }

    const before = text.substring(0, wordPosition).trim();
    const bold = text.substring(wordPosition, wordPosition + wordLength);
    const after = text.substring(wordPosition + wordLength).trim();

    const html = before + '<span class="text-primary-500 dark:text-primary-500 font-bold">' + bold + '</span>' + after;
    return sanitiseHtml(html);
  });

  const handleReveal = () => {
    if (isNsfw && !isRevealed.value) {
      isRevealed.value = true;
    }
  };

  async function favouriteSentence() {
    if (props.wordId == null || props.readingIndex == null) return;

    const { text, wordPosition, wordLength, sourceDeckParent, sourceDeck } = props.exampleSentence;
    const before = text.substring(0, wordPosition);
    const word = text.substring(wordPosition, wordPosition + wordLength);
    const after = text.substring(wordPosition + wordLength);
    const markedText = `${before}**${word}**${after}`;

    let source = '';
    if (sourceDeckParent) source += localiseTitle(sourceDeckParent) + ' - ';
    if (sourceDeck) source += localiseTitle(sourceDeck);

    try {
      await $api(`user/example-sentences/${props.wordId}/${props.readingIndex}/favourite`, {
        method: 'POST',
        body: { text: markedText, source: source || undefined },
      });
      favourited.value = true;
      emit('favourited');
    } catch {
      toast.add({ severity: 'error', summary: 'Maximum of 3 custom sentences reached', life: 3000 });
    }
  }
</script>

<template>
  <div class="flex flex-col">
    <blockquote class="relative inline-block border-l-4 border-primary-500 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r shadow-sm overflow-hidden">
      <div class="flex items-start gap-2">
        <div v-html="formattedText" class="md:text-lg text-sm transition-filter duration-200 flex-1" lang="ja" :class="{ 'blur-sm': isNsfw && !isRevealed }" @click="handleReveal"></div>
        <TtsButton :text="exampleSentence.text" :sentence-id="exampleSentence.sentenceId" type="sentence" size="sm" class="mt-0.5 shrink-0" />
        <button
          v-if="authStore.isAuthenticated && wordId != null && readingIndex != null"
          class="inline-flex items-center justify-center transition-colors mt-0.5 shrink-0"
          :class="favourited ? 'text-yellow-500' : 'text-surface-400 hover:text-yellow-500'"
          :disabled="favourited"
          title="Save as custom sentence"
          @click="favouriteSentence"
        >
          <i class="pi text-sm" :class="favourited ? 'pi-star-fill' : 'pi-star'" />
        </button>
      </div>
      <div
        v-if="isNsfw && !isRevealed"
        class="absolute top-0 left-0 w-full h-full flex items-center justify-center cursor-pointer z-10"
        role="button"
        tabindex="0"
        aria-label="Reveal potentially not safe for work text"
        @click="handleReveal"
        @keydown.enter="handleReveal"
        @keydown.space.prevent="handleReveal"
      >
        <div class="text-center px-3 py-2 bg-white/80 backdrop-blur-md border border-red-300 text-red-600 text-sm font-semibold rounded shadow">
          This text is potentially not safe for work. Click to reveal.
        </div>
      </div>
    </blockquote>
    <div v-if="showSource" class="flex items-center mb-2">
      <span class="text-xs italic mr-2 ml-4">Source:</span>
      <div class="inline-flex items-center text-xs flex-wrap">
        <NuxtLink
          v-if="exampleSentence.sourceDeckParent != null"
          :to="`/decks/media/${exampleSentence.sourceDeckParent.deckId}/detail`"
          target="_blank"
          class="hover:underline text-primary-600"
        >
          {{ localiseTitle(exampleSentence.sourceDeckParent) }}
        </NuxtLink>
        <span v-if="exampleSentence.sourceDeckParent != null" class="mx-1">-</span>
        <NuxtLink
          v-if="exampleSentence.sourceDeck != null"
          :to="`/decks/media/${exampleSentence.sourceDeck.deckId}/detail`"
          target="_blank"
          class="hover:underline text-primary-600"
        >
          {{ localiseTitle(exampleSentence.sourceDeck) }}
        </NuxtLink>
        &nbsp;
        ({{getMediaTypeText(exampleSentence.sourceDeck.mediaType)}})
      </div>
    </div>
  </div>
</template>

<style scoped></style>
