<script setup lang="ts">
  import type { ExampleSentence } from '~/types';
  import { computed, ref } from 'vue';
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{
    exampleSentence: ExampleSentence;
    showSource?: boolean;
    wordId?: number;
    readingIndex?: number;
    // The user already has the max number of custom sentences for this word — disable the create paths.
    atLimit?: boolean;
    // Marked text of the custom sentences already saved for this word, so a sentence saved in an
    // earlier visit still shows as starred.
    savedTexts?: string[];
  }>();

  const emit = defineEmits<{
    favourited: [];
  }>();

  const { $api } = useNuxtApp();
  const authStore = useAuthStore();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();
  const store = useJitenStore();
  const isNsfw = isTextNsfw(props.exampleSentence.text);
  const { limits: planLimits } = useJitenPlus();
  const sentenceLimitMessage = computed(() => `Maximum of ${planLimits.value.customSentencesPerWord} custom sentences reached`);
  const savedLocally = ref(false);
  const revealedLocally = ref(false);
  const isRevealed = computed(() => store.displayAllNsfw || revealedLocally.value);

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
      revealedLocally.value = true;
    }
  };

  const canEdit = computed(() => authStore.isAuthenticated && props.wordId != null && props.readingIndex != null);
  const editing = ref(false);

  const markedText = computed(() => {
    const { text, wordPosition, wordLength } = props.exampleSentence;
    if (wordPosition < 0 || wordLength <= 0 || wordPosition >= text.length) return text;
    const before = text.substring(0, wordPosition);
    const word = text.substring(wordPosition, wordPosition + wordLength);
    const after = text.substring(wordPosition + wordLength);
    return `${before}**${word}**${after}`;
  });

  const favourited = computed(() => savedLocally.value || (props.savedTexts?.includes(markedText.value) ?? false));

  const sentenceSource = computed(() => {
    const { sourceDeckParent, sourceDeck } = props.exampleSentence;
    let source = '';
    if (sourceDeckParent) source += localiseTitle(sourceDeckParent) + ' - ';
    if (sourceDeck) source += localiseTitle(sourceDeck);
    return clampSentenceSource(source);
  });

  // Editing a corpus sentence creates a new custom (favourite) sentence; reuse the favourite flow.
  function onEdited() {
    editing.value = false;
    savedLocally.value = true;
    emit('favourited');
  }

  async function favouriteSentence() {
    if (props.wordId == null || props.readingIndex == null) return;

    try {
      await $api(`user/example-sentences/${props.wordId}/${props.readingIndex}/favourite`, {
        method: 'POST',
        body: { text: markedText.value, source: sentenceSource.value || undefined },
      });
      savedLocally.value = true;
      emit('favourited');
    } catch (e) {
      const data = (e as { data?: unknown })?.data;
      toast.add({ severity: 'error', summary: typeof data === 'string' && data ? data : sentenceLimitMessage.value, life: 3000 });
    }
  }
</script>

<template>
  <div class="flex flex-col">
    <InlineSentenceEditor
      v-if="editing"
      :word-id="wordId"
      :reading-index="readingIndex"
      :initial-text="markedText"
      :initial-source="sentenceSource"
      :user-sentence-id="null"
      class="mb-2"
      @saved="onEdited"
      @cancel="editing = false"
    />
    <template v-else>
    <blockquote class="relative inline-block border-l-4 border-primary-500 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r shadow-sm overflow-hidden">
      <div class="flex items-start gap-2">
        <div v-html="formattedText" class="md:text-lg text-sm transition-filter duration-200 flex-1" lang="ja" :class="{ 'blur-sm': isNsfw && !isRevealed }" @click="handleReveal"></div>
        <TtsButton :text="exampleSentence.text" :sentence-id="exampleSentence.sentenceId" type="sentence" size="sm" class="mt-0.5 shrink-0" />
        <button
          v-if="canEdit"
          class="inline-flex items-center justify-center transition-colors mt-0.5 shrink-0"
          :class="favourited ? 'text-yellow-500' : atLimit ? 'text-surface-300 dark:text-surface-400 cursor-not-allowed' : 'text-surface-400 hover:text-yellow-500'"
          :disabled="favourited || atLimit"
          :title="atLimit ? sentenceLimitMessage : 'Save as custom sentence'"
          @click="favouriteSentence"
        >
          <i class="pi text-sm" :class="favourited ? 'pi-star-fill' : 'pi-star'" />
        </button>
        <button
          v-if="canEdit"
          class="inline-flex items-center justify-center transition-colors mt-0.5 shrink-0"
          :class="atLimit ? 'text-surface-300 dark:text-surface-400 cursor-not-allowed' : 'text-surface-400 hover:text-primary-500 cursor-pointer'"
          :disabled="atLimit"
          :title="atLimit ? sentenceLimitMessage : 'Edit sentence'"
          @click="editing = true"
        >
          <i class="pi pi-pencil text-sm" />
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
    </template>
  </div>
</template>

<style scoped></style>
