<script setup lang="ts">
  import type { CardLayoutBlock, EtymologyBlockOptions } from '~/types';
  import { etymologyDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';
  import CardBlockSpoiler from './CardBlockSpoiler.vue';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<EtymologyBlockOptions>(etymologyDefaults, props.block.options));

  const { wordData, isPreview, sample } = useCardContext();

  const languageSources = computed(() => (isPreview ? (sample?.languageSources ?? []) : (wordData.value?.languageSources ?? [])));

  const LANG_NAMES: Record<string, string> = {
    eng: 'English',
    por: 'Portuguese',
    dut: 'Dutch',
    fre: 'French',
    ger: 'German',
    ita: 'Italian',
    spa: 'Spanish',
    rus: 'Russian',
    chi: 'Chinese',
    kor: 'Korean',
    lat: 'Latin',
    gre: 'Greek',
    ara: 'Arabic',
    heb: 'Hebrew',
    san: 'Sanskrit',
    tha: 'Thai',
    vie: 'Vietnamese',
    tur: 'Turkish',
    pol: 'Polish',
    swe: 'Swedish',
    nor: 'Norwegian',
    hun: 'Hungarian',
    haw: 'Hawaiian',
    afr: 'Afrikaans',
  };

  const hasWasei = computed(() => languageSources.value.some((s) => s.isWasei));
  const etymologyLine = computed(() => {
    const sources = languageSources.value;
    if (!sources || sources.length === 0) return '';
    const parts = sources
      .map((s) => {
        const name = LANG_NAMES[s.lang] ?? s.lang;
        return s.text ? `${name} ${s.text}` : name;
      })
      .filter((p) => p.length > 0);
    return parts.length > 0 ? `from ${parts.join(' + ')}` : '';
  });
</script>

<template>
  <CardBlockSpoiler v-if="languageSources.length" :enabled="opts.spoiler">
    <div class="mb-3 flex flex-wrap items-center justify-center gap-2">
      <span
        v-if="hasWasei"
        class="inline-block rounded-full px-2 py-0.5 text-xs font-medium bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300"
        title="Japanese-made — constructed in Japanese from foreign words, not a real foreign phrase"
        >和製 wasei</span
      >
      <span v-if="etymologyLine" class="text-sm text-gray-500 dark:text-gray-400">{{ etymologyLine }}</span>
    </div>
  </CardBlockSpoiler>
</template>
