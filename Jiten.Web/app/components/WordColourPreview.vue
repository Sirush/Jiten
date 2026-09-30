<script setup lang="ts">
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import type { WordStateColourKey } from '~/utils/wordState';

  /** The colour row being hovered or edited, whose words get underlined. */
  defineProps<{ highlight: WordStateColourKey | null }>();

  const store = useJitenStore();
  const auth = useAuthStore();

  const tokens: { text: string; state: WordStateColourKey }[] = [
    { text: '昨日', state: 'mature' },
    { text: '、', state: 'mature' },
    { text: '友達', state: 'young' },
    { text: 'の', state: 'mature' },
    { text: 'タロウ', state: 'ignored' },
    { text: 'と', state: 'mature' },
    { text: '映画館', state: 'new' },
    { text: 'で', state: 'mature' },
    { text: '怪獣', state: 'due' },
    { text: '映画', state: 'mature' },
    { text: 'を', state: 'mature' },
    { text: '観た', state: 'redundant' },
    { text: '。', state: 'mature' },
  ];

  const colourElsewhere = computed(() => store.colourWordsByState && auth.isAuthenticated);
  const colourOf = (state: WordStateColourKey, enabled: boolean) => (enabled ? (store.resolvedStateColours[state] ?? undefined) : undefined);
</script>

<template>
  <figure class="grid grid-cols-1 gap-3 md:grid-cols-2" aria-label="Preview of your word colours">
    <div class="flex flex-col gap-1.5">
      <span class="text-xs text-surface-600 dark:text-surface-400">Subtitles on the watch page</span>
      <p class="rounded bg-black px-3 py-3 text-center text-lg text-white dark:ring-1 dark:ring-surface-700" lang="ja">
        <span
          v-for="(token, i) in tokens"
          :key="i"
          :class="{ 'colour-highlight': highlight === token.state }"
          :style="{ color: colourOf(token.state, true) }"
          >{{ token.text }}</span
        >
      </p>
    </div>
    <div class="flex flex-col gap-1.5">
      <span class="text-xs text-surface-600 dark:text-surface-400">
        {{ colourElsewhere ? 'Example sentences and vocabulary lists' : 'Example sentences, with “Colour words everywhere” off' }}
      </span>
      <p class="rounded border border-surface-200 px-3 py-3 text-lg text-surface-900 dark:border-surface-700 dark:text-surface-0" lang="ja">
        <span
          v-for="(token, i) in tokens"
          :key="i"
          :class="{ 'colour-highlight': colourElsewhere && highlight === token.state }"
          :style="{ color: colourOf(token.state, colourElsewhere) }"
          >{{ token.text }}</span
        >
      </p>
    </div>
  </figure>
</template>

<style scoped>
  .colour-highlight {
    text-decoration: underline;
    text-decoration-thickness: 2px;
    text-underline-offset: 0.3em;
  }
</style>
