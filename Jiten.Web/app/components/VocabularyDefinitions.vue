<script setup lang="ts">
  import type { Definition, Reading } from '~/types';

  const props = defineProps<{
    definitions: Definition[];
    isCompact: boolean;
    currentReadingIndex?: number;
    readings?: Reading[];
  }>();

  const store = useJitenStore();
  const hideDefinition = computed({
    get: () => store.hideVocabularyDefinitions,
    set: (value) => {
      store.hideVocabularyDefinitions = value;
    },
  });

  const readingTextByIndex = computed(() => {
    const map = new Map<number, string>();
    if (props.readings) {
      for (const r of props.readings) {
        map.set(r.readingIndex, r.text);
      }
    }
    return map;
  });

  function isRestricted(definition: Definition): boolean {
    if (props.currentReadingIndex == null || !definition.restrictedToReadingIndices) return false;
    return !definition.restrictedToReadingIndices.includes(props.currentReadingIndex);
  }

  function restrictedLabel(definition: Definition): string | null {
    if (!definition.restrictedToReadingIndices || definition.restrictedToReadingIndices.length === 0) return null;
    const names = definition.restrictedToReadingIndices
      .map((idx) => readingTextByIndex.value.get(idx) ?? `form ${idx}`)
      .join(', ');
    return `only applies to ${names}`;
  }

  const samePartsOfSpeech = (a: string[] | null | undefined, b: string[] | null | undefined) =>
    a === b || (a != null && b != null && a.length === b.length && a.every((v, i) => v === b[i]));

  const definitionsWithPartsOfSpeech = computed(() => {
    if (!Array.isArray(props.definitions)) {
      return [];
    }
    let previousPartOfSpeech: string[] | null = null;

    return props.definitions.map((definition) => {
      const isDifferentPartOfSpeech = !samePartsOfSpeech(previousPartOfSpeech, definition.partsOfSpeech);
      previousPartOfSpeech = definition.partsOfSpeech;
      return {
        ...definition,
        isDifferentPartOfSpeech,
      };
    });
  });
</script>

<template>
  <div v-if="!isCompact">
    <ul>
      <li v-for="definition in definitionsWithPartsOfSpeech" :key="definition.index" :class="{ 'opacity-40': isRestricted(definition) }">
        <div v-if="definition.isDifferentPartOfSpeech" class="flex flex-wrap gap-1 mt-1 mb-0.5">
          <Tooltip v-for="pos in definition.partsOfSpeech" :key="pos" :content="pos" placement="top">
            <span
              class="pos-badge"
              :class="`pos-${posColorClass(abbreviatePos(pos))}`"
            >{{ abbreviatePos(pos) }}</span>
          </Tooltip>
        </div>
        <span class="text-gray-400">{{ definition.index }}.</span> {{ definition.meanings.join('; ') }}
        <span v-for="f in definition.field" :key="f" class="ml-1 inline-block rounded-full px-2 py-0.5 text-xs bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300">{{ f }}</span>
        <span v-for="d in definition.dial" :key="d" class="ml-1 inline-block rounded-full px-2 py-0.5 text-xs bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">{{ d }}</span>
        <span v-if="restrictedLabel(definition)" class="ml-1 inline-block rounded-full px-2 py-0.5 text-xs bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-400">{{ restrictedLabel(definition) }}</span>
      </li>
    </ul>
  </div>

  <div v-if="isCompact && !hideDefinition">
    <span v-for="definition in definitionsWithPartsOfSpeech.slice(0, 10)" :key="definition.index" :class="{ 'opacity-40': isRestricted(definition) }">
      {{ definition.meanings.join('; ') }}
      <span v-if="definition.index !== Math.min(definitionsWithPartsOfSpeech.length, 10)">; </span>
    </span>
  </div>
</template>

<style scoped></style>
