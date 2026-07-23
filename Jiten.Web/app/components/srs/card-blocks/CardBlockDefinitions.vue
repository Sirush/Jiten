<script setup lang="ts">
  import type { CardLayoutBlock, DefinitionsBlockOptions } from '~/types';
  import { definitionsDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';
  import CardBlockSpoiler from './CardBlockSpoiler.vue';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<DefinitionsBlockOptions>(definitionsDefaults, props.block.options));

  const { card, wordData, wordLoading, isPreview, sample, registerDictCycler } = useCardContext();

  const sizeClass = computed(() => (opts.value.size === 'small' ? 'text-sm' : opts.value.size === 'large' ? 'text-lg' : ''));

  const currentReadingIndex = computed(() => card.value?.readingIndex ?? 0);

  const { resolvedGroups } = useDictionaryDefinitions(
    computed(() => wordData.value?.mainReading?.text),
    computed(() => wordData.value?.definitions)
  );

  const fallbackDefinitions = computed(() => {
    let previousPos: string | null = null;
    return (card.value?.definitions ?? []).map((def) => {
      const posKey = JSON.stringify(def.partsOfSpeech);
      const showPos = def.partsOfSpeech.length > 0 && posKey !== previousPos;
      previousPos = posKey;
      return { ...def, showPos };
    });
  });

  // Client-side "Show N more" for the pre-load fallback list; resets whenever the card changes.
  const fallbackExpanded = ref(false);
  watch(
    () => (card.value ? `${card.value.wordId}-${card.value.readingIndex}` : ''),
    () => {
      fallbackExpanded.value = false;
    }
  );
  const fallbackLimit = computed(() => (opts.value.maxDefinitions && opts.value.maxDefinitions > 0 ? opts.value.maxDefinitions : null));

  // Re-collapses when the limit changes so toggling the option in the editor demonstrates it each time.
  const previewExpanded = ref(false);
  watch(fallbackLimit, () => {
    previewExpanded.value = false;
  });
  const visiblePreview = computed(() => {
    const defs = sample?.definitions ?? [];
    return fallbackLimit.value && !previewExpanded.value ? defs.slice(0, fallbackLimit.value) : defs;
  });
  const previewHiddenCount = computed(() => (sample?.definitions.length ?? 0) - visiblePreview.value.length);
  const visibleFallback = computed(() =>
    fallbackLimit.value && !fallbackExpanded.value ? fallbackDefinitions.value.slice(0, fallbackLimit.value) : fallbackDefinitions.value
  );
  const fallbackHiddenCount = computed(() => Math.max(0, fallbackDefinitions.value.length - visibleFallback.value.length));

  const dictDefinitionsRef = ref<{ cycleDictionary: (direction: 1 | -1) => void } | null>(null);
  onMounted(() => registerDictCycler((direction) => dictDefinitionsRef.value?.cycleDictionary(direction)));
  onUnmounted(() => registerDictCycler(null));
</script>

<template>
  <CardBlockSpoiler :enabled="opts.spoiler">
    <div v-if="isPreview" class="mb-4" :class="sizeClass">
      <div class="flex flex-wrap gap-1 mt-2 mb-0.5">
        <span class="pos-badge pos-blue">{{ sample!.pos }}</span>
      </div>
      <div v-for="(def, i) in visiblePreview" :key="i"><span class="text-gray-400">{{ i + 1 }}.</span> {{ def }}</div>
      <button
        v-if="previewHiddenCount > 0"
        type="button"
        class="mt-1 text-xs text-primary-600 dark:text-primary-400 hover:underline"
        @click.stop="previewExpanded = true"
      >
        Show {{ previewHiddenCount }} more
      </button>
    </div>
    <div v-else class="mb-4" :class="sizeClass">
      <template v-if="wordData">
        <ClientOnly>
          <VocabularyDictionaryDefinitions
            ref="dictDefinitionsRef"
            :resolved-groups="resolvedGroups"
            :arrow-key-nav="false"
            :is-compact="false"
            :max-definitions="opts.maxDefinitions"
            :current-reading-index="currentReadingIndex"
            :readings="wordData.alternativeReadings"
          />
          <template #fallback>
            <VocabularyDefinitions
              :definitions="wordData.definitions"
              :is-compact="false"
              :max-definitions="opts.maxDefinitions"
              :current-reading-index="currentReadingIndex"
              :readings="wordData.alternativeReadings"
            />
          </template>
        </ClientOnly>
      </template>
      <template v-else>
        <div v-for="def in visibleFallback" :key="def.index">
          <div v-if="def.showPos" class="flex flex-wrap gap-1 mt-2 mb-0.5">
            <Tooltip v-for="pos in def.partsOfSpeech" :key="pos" :content="pos" placement="top">
              <span class="pos-badge" :class="`pos-${posColorClass(abbreviatePos(pos))}`">{{ abbreviatePos(pos) }}</span>
            </Tooltip>
          </div>
          <div>
            <span class="text-gray-400">{{ def.index }}.</span> {{ def.meanings.join('; ') }}
          </div>
        </div>
        <button
          v-if="fallbackHiddenCount > 0"
          type="button"
          class="mt-1 text-xs text-primary-600 dark:text-primary-400 hover:underline"
          @click.stop="fallbackExpanded = true"
        >
          Show {{ fallbackHiddenCount }} more
        </button>
        <div v-if="wordLoading" class="flex items-center gap-1.5 mt-2 text-xs text-gray-400">
          <Icon name="svg-spinners:ring-resize" size="0.875rem" />
          <span>Loading full entry…</span>
        </div>
      </template>
    </div>
  </CardBlockSpoiler>
</template>

<style scoped>
  :deep([data-pc-name='tabs']),
  :deep([data-pc-name='tabpanels']),
  :deep([data-pc-name='tabpanel']),
  :deep([data-pc-name='tablist']) {
    background: transparent !important;
  }
</style>
