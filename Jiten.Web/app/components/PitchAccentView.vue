<script setup lang="ts">
  import type { PitchAccentDisplay } from '~/utils/displayProfile';

  const props = defineProps<{
    reading: string;
    accents: number[];
    whenHidden?: Exclude<PitchAccentDisplay, 'hidden'>;
    numbersBesideWord?: boolean;
  }>();

  const store = useJitenStore();
  const mode = computed(() => (store.pitchAccentDisplay === 'hidden' ? (props.whenHidden ?? 'hidden') : store.pitchAccentDisplay));
  const numbersWithGraph = computed(() => mode.value === 'both' && !props.numbersBesideWord);
</script>

<template>
  <PitchAccentNumbers v-if="mode === 'number'" :accents="accents" :reading="reading" />
  <ClientOnly v-else-if="mode === 'graph' || mode === 'both'">
    <div class="flex flex-wrap items-center gap-x-6 gap-y-2">
      <div v-for="accent in accents" :key="accent" class="flex items-center gap-1">
        <PitchAccentNumbers v-if="numbersWithGraph" :accents="[accent]" :reading="reading" />
        <LazyPitchDiagram :reading="reading" :pitch-accent="accent" />
      </div>
    </div>
  </ClientOnly>
</template>
