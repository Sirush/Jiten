<script setup lang="ts">
  import { moraCount, pitchCategory, readingKana } from '~/utils/pitchAccent';

  const props = defineProps<{
    accents: number[];
    reading?: string;
  }>();

  const store = useJitenStore();
  const morae = computed(() => (props.reading ? moraCount(readingKana(props.reading)) : 0));
  const categoryClass = (accent: number) => {
    if (!store.pitchAccentColours || !morae.value) return '';
    const category = pitchCategory(accent, morae.value);
    return category ? `pitch-${category} pitch-number` : '';
  };
</script>

<template>
  <span class="inline-flex flex-wrap gap-1" aria-label="Pitch accent numbers">
    <Tooltip v-for="accent in accents" :key="accent" :content="accent === 0 ? 'Heiban: no drop in pitch' : `Pitch drops after mora ${accent}`">
      <span
        class="inline-block rounded border border-surface-300 dark:border-surface-600 px-1.5 text-sm font-medium tabular-nums leading-6 text-surface-700 dark:text-surface-200 cursor-help"
        :class="categoryClass(accent)"
        >[{{ accent }}]</span
      >
    </Tooltip>
  </span>
</template>

<style scoped>
  .pitch-number {
    color: var(--pitch-colour);
    border-color: var(--pitch-colour);
  }
</style>
