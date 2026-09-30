<script setup lang="ts">
  import { KnownState, type SentenceFurigana } from '~/types';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import { sentenceRubyHtml, targetWordId, visibleFurigana } from '~/utils/sentenceRuby';
  import { sanitiseHtml } from '~/utils/sanitiseHtml';
  import { pitchColourClasses } from '~/utils/pitchAccent';

  const store = useJitenStore();
  const auth = useAuthStore();
  const convertToRuby = useConvertToRuby();
  const stateColour = useWordStateColour();

  const known = [KnownState.Mature];
  const unknown: KnownState[] = [];
  const words = [
    { ruby: '食[た]べる', reading: 'たべる', pitch: 2, states: known, caption: 'A word you know' },
    { ruby: '林檎[りんご]', reading: 'りんご', pitch: 0, states: unknown, caption: 'A word you don’t know' },
  ];

  const sentence = '昨日、林檎を食べた。';
  const groups: SentenceFurigana[] = [
    { position: 0, length: 2, reading: 'きのう', wordId: 1, known: true, states: known },
    { position: 3, length: 2, reading: 'りんご', wordId: 2, known: false, states: unknown },
    { position: 6, length: 1, reading: 'た', wordId: 3, known: true, states: known },
  ];

  const target = { position: 3, length: 2 };

  const sentenceHtml = computed(() => {
    const mode = store.sentenceFurigana === 'unknown' && !auth.isAuthenticated ? 'all' : store.sentenceFurigana;
    const hiddenWordId = mode === 'exceptTarget' ? targetWordId(groups, target.position, target.length) : undefined;
    const shown = new Set(visibleFurigana(groups, mode, hiddenWordId));
    const coloured = store.colourWordsByState && auth.isAuthenticated;
    return sanitiseHtml(
      sentenceRubyHtml(sentence, target.position, target.length, groups, {
        showReading: (g) => shown.has(g),
        revealOnHover: store.furiganaOnHover ? () => true : undefined,
        colourOf: coloured ? (g) => stateColour(g.states)?.color ?? null : undefined,
      })
    );
  });
</script>

<template>
  <figure class="rounded border border-dashed border-surface-300 p-3 dark:border-surface-600" aria-label="Preview of your Japanese text settings">
    <div class="flex flex-wrap items-end gap-x-8 gap-y-3">
      <div v-for="word in words" :key="word.ruby" class="flex flex-col">
        <span class="flex items-baseline gap-2">
          <span
            class="leading-relaxed"
            :class="[headwordSizeClass(word.ruby, true, store.headwordSize), store.pitchAccentColours ? pitchColourClasses(word.ruby, [word.pitch]) : '']"
            :style="stateColour(word.states)"
            lang="ja"
            v-html="convertToRuby(word.ruby, undefined, word.states)"
          />
          <PitchAccentNumbers
            v-if="store.pitchAccentDisplay === 'number' || store.pitchAccentDisplay === 'both'"
            :reading="word.reading"
            :accents="[word.pitch]"
          />
        </span>
        <span class="text-xs text-surface-600 dark:text-surface-400">{{ word.caption }}</span>
        <PitchAccentView v-if="store.pitchAccentDisplay === 'graph' || store.pitchAccentDisplay === 'both'" :reading="word.reading" :accents="[word.pitch]" numbers-beside-word class="mt-1" />
      </div>
    </div>
    <p class="mt-3 border-l-4 border-primary-500 pl-3" :class="sentenceSizeClass(store.sentenceSize)" lang="ja" v-html="sentenceHtml" />
    <p class="mt-3 border-t border-surface-200 pt-3 text-lg leading-relaxed text-surface-900 dark:border-surface-700 dark:text-surface-0" lang="ja">
      言心令直糸 さきふそ<br />
      カタカナ ヴァッショー<br />
      ０１２３４５ ＡＢＣｘｙｚ 123 ABC
    </p>
  </figure>
</template>
