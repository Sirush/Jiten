<script setup lang="ts">
  import type { Kanji, WordSummary } from '~/types';
  import { type KanjiScale, kanjiScaleMembership } from '~/data/kanjiGroupings';

  type BadgeSeverity = 'primary' | 'info' | 'secondary' | 'success' | 'warn' | 'danger' | 'contrast';

  const scaleSeverities: Record<KanjiScale, BadgeSeverity> = {
    jlpt: 'info',
    grade: 'secondary',
    kanken: 'success',
    wanikani: 'warn',
    rtk: 'danger',
    klc: 'primary',
    tmw: 'secondary',
  };

  const scaleClasses: Partial<Record<KanjiScale, string>> = {
    tmw: 'p-tag-teal',
  };

  const allScales: KanjiScale[] = ['jlpt', 'grade', 'kanken', 'wanikani', 'rtk', 'klc', 'tmw'];

  // A route param that isn't a single character can never be a kanji; 404 before fetching so the
  // SPA catch-all can't answer 200 for arbitrary paths (which Google then treats as soft 404s).
  definePageMeta({
    validate: (route) => [...String(route.params.character)].length === 1,
  });

  const route = useRoute();
  const { $api } = useNuxtApp();

  const character = computed(() => {
    const c = route.params.character;
    return typeof c === 'string' ? c : c[0];
  });

  const { data: kanji, status, error, ready } = useApiFetch<Kanji>(() => `kanji/${encodeURIComponent(character.value)}`);

  // Only a definitive 404 becomes a 404 page: an SSR timeout or 5xx must keep rendering the normal
  // error state at 200, or a slow API would deindex working pages.
  if (import.meta.server) {
    await ready;
    if (isMissingResource(error.value, kanji.value)) throw createError({ statusCode: 404, statusMessage: 'Kanji not found', fatal: true });
  }

  const scaleBadges = computed(() =>
    allScales
      .map((scale) => ({
        severity: scaleSeverities[scale],
        cls: scaleClasses[scale],
        text: kanjiScaleMembership(character.value, scale, kanji.value?.grade),
      }))
      .filter((b): b is { severity: BadgeSeverity; cls?: string; text: string } => b.text != null)
  );

  // undefined = untouched, so the most-used reading renders expanded (server-side, and instead of an
  // empty section). null = explicitly collapsed by the user.
  const expandedReading = ref<string | null | undefined>(undefined);
  const activeReading = computed(() => (expandedReading.value === undefined ? (kanji.value?.wordsByReading?.[0]?.reading ?? null) : expandedReading.value));
  const allReadingWords = ref<WordSummary[] | null>(null);
  const allTopWords = ref<WordSummary[] | null>(null);
  const loadingReadingWords = ref(false);
  const loadingTopWords = ref(false);

  const toggleReading = (reading: string) => {
    if (activeReading.value === reading) {
      expandedReading.value = null;
    } else {
      expandedReading.value = reading;
      allReadingWords.value = null;
    }
  };

  const totalReadingWords = computed(() => {
    if (!kanji.value?.wordsByReading) return 0;
    return kanji.value.wordsByReading.reduce((sum, g) => sum + g.totalWords, 0);
  });

  const readingPercent = (count: number) => {
    if (totalReadingWords.value === 0) return '0';
    return ((count / totalReadingWords.value) * 100).toFixed(0);
  };

  const expandedGroup = computed(() => {
    if (!kanji.value?.wordsByReading || !activeReading.value) return null;
    return kanji.value.wordsByReading.find((g) => g.reading === activeReading.value) ?? null;
  });

  const expandedWords = computed(() => {
    if (!expandedGroup.value) return [];
    if (allReadingWords.value) return allReadingWords.value;
    return expandedGroup.value.words.slice(0, 10);
  });

  const visibleTopWords = computed(() => {
    if (allTopWords.value) return allTopWords.value;
    if (!kanji.value?.topWords) return [];
    return kanji.value.topWords;
  });

  const loadAllReadingWords = async () => {
    if (!activeReading.value) return;
    loadingReadingWords.value = true;
    try {
      const data = await $api<{ items: WordSummary[] }>(`kanji/${encodeURIComponent(character.value)}/words`, {
        query: { reading: activeReading.value, pageSize: 5000 },
      });
      allReadingWords.value = data.data;
    } finally {
      loadingReadingWords.value = false;
    }
  };

  const collapseReadingWords = () => {
    allReadingWords.value = null;
  };

  const loadAllTopWords = async () => {
    loadingTopWords.value = true;
    try {
      const data = await $api<{ items: WordSummary[] }>(`kanji/${encodeURIComponent(character.value)}/words`, { query: { pageSize: 5000 } });
      allTopWords.value = data.data;
    } finally {
      loadingTopWords.value = false;
    }
  };

  const collapseTopWords = () => {
    allTopWords.value = null;
  };

  const headlineMeaning = computed(() => kanji.value?.meanings.slice(0, 3).join(', ') ?? '');

  const metaDescription = computed(() => {
    const k = kanji.value;
    if (!k) return `Meaning, readings and common words for the kanji ${character.value}.`;

    const level = [k.grade != null ? `grade ${k.grade}` : null, k.jlptLevel != null ? `JLPT N${k.jlptLevel}` : null].filter(Boolean).join(', ');

    const readings = [...k.onReadings, ...k.kunReadings].slice(0, 4).join(', ');
    const sentence = `${k.character} is a ${level ? `${level} ` : ''}kanji meaning ${k.meanings.slice(0, 3).join(', ')}.`;
    const detail = `${k.strokeCount} strokes${k.frequencyRank ? `, frequency rank #${k.frequencyRank}` : ''}`;
    return `${sentence} Readings: ${readings}. ${detail}.`;
  });

  // Two meanings keeps the title inside Google's display width; the h1 carries the fuller list.
  const titleMeaning = computed(() => kanji.value?.meanings.slice(0, 2).join(', ') ?? '');

  useSeoMeta({
    title: () => (titleMeaning.value ? `${character.value} Kanji: ${titleMeaning.value} - Readings and Common Words` : `${character.value} - Kanji`),
    description: metaDescription,
    ogDescription: metaDescription,
  });
</script>

<template>
  <div class="max-w-4xl mx-auto px-4 py-8">
    <div v-if="status === 'pending'" class="flex justify-center py-12">
      <ProgressSpinner />
    </div>

    <div v-else-if="status === 'error'" class="text-center py-12">
      <p class="text-surface-500 dark:text-surface-400">Kanji not found</p>
    </div>

    <div v-else-if="kanji" class="space-y-2">
      <!-- Main kanji display -->
      <div class="text-center">
        <h1>
          <span class="block text-9xl font-bold mb-2" lang="ja">{{ kanji.character }}</span>
          <span v-if="headlineMeaning" class="block text-base font-normal mb-4 text-surface-600 dark:text-surface-400">
            Kanji meaning "{{ headlineMeaning }}"
          </span>
        </h1>

        <!-- Metadata badges -->
        <div class="flex flex-wrap justify-center gap-2 mb-4">
          <Tag v-if="kanji.frequencyRank" severity="primary">Jiten frequency #{{ kanji.frequencyRank }}</Tag>
          <Tag v-for="badge in scaleBadges" :key="badge.text" :severity="badge.severity" :class="badge.cls">{{ badge.text }}</Tag>
          <Tag severity="secondary">{{ kanji.strokeCount }} strokes</Tag>
        </div>
      </div>

      <!-- Meanings -->
      <div class="border-surface-200 dark:border-surface-700 border rounded-lg p-4">
        <h2 class="text-lg font-semibold mb-2">Meanings</h2>
        <p class="text-surface-700 dark:text-surface-300">
          {{ kanji.meanings.join(', ') }}
        </p>
      </div>

      <!-- On'yomi / Kun'yomi -->
      <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div v-if="kanji.onReadings.length > 0" class="border-surface-200 dark:border-surface-700 border rounded-lg p-4">
          <h2 class="text-lg font-semibold mb-2">On'yomi</h2>
          <div class="flex flex-wrap gap-2">
            <Tag v-for="reading in kanji.onReadings" :key="reading" severity="primary" :value="reading" />
          </div>
        </div>
        <div v-if="kanji.kunReadings.length > 0" class="border-surface-200 dark:border-surface-700 border rounded-lg p-4">
          <h2 class="text-lg font-semibold mb-2">Kun'yomi</h2>
          <div class="flex flex-wrap gap-2">
            <Tag v-for="reading in kanji.kunReadings" :key="reading" severity="secondary" :value="reading" />
          </div>
        </div>
      </div>

      <!-- Reading usage in words -->
      <div v-if="kanji.wordsByReading?.length" class="border-surface-200 dark:border-surface-700 border rounded-lg p-4">
        <h2 class="text-lg font-semibold mb-3">Reading usage</h2>

        <div class="flex flex-wrap gap-2 mb-2">
          <button
            v-for="group in kanji.wordsByReading"
            :key="group.reading"
            class="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-sm transition-colors cursor-pointer"
            :class="
              activeReading === group.reading
                ? 'bg-primary-100 dark:bg-primary-900 text-primary-800 dark:text-primary-200'
                : 'bg-surface-100 dark:bg-surface-800 text-surface-700 dark:text-surface-300 hover:bg-surface-200 dark:hover:bg-surface-700'
            "
            @click="toggleReading(group.reading)"
          >
            <span lang="ja" class="font-medium">{{ group.reading }}</span>
            <span class="text-xs opacity-70">{{ group.totalWords }} ({{ readingPercent(group.totalWords) }}%)</span>
          </button>
        </div>

        <div v-if="expandedGroup" class="mt-3">
          <div class="space-y-1">
            <KanjiWordRow v-for="word in expandedWords" :key="`${word.wordId}-${word.readingIndex}`" :word="word" />
          </div>
          <div v-if="expandedGroup.totalWords > 10" class="mt-2">
            <button
              v-if="!allReadingWords"
              class="text-sm text-primary-600 dark:text-primary-400 hover:underline cursor-pointer"
              :disabled="loadingReadingWords"
              @click="loadAllReadingWords"
            >
              {{ loadingReadingWords ? 'Loading...' : `View all ${expandedGroup.totalWords}` }}
            </button>
            <button v-else class="text-sm text-primary-600 dark:text-primary-400 hover:underline cursor-pointer" @click="collapseReadingWords">
              View less
            </button>
          </div>
        </div>
      </div>

      <!-- Most common words -->
      <div v-if="kanji.topWords && kanji.topWords.length > 0" class="border-surface-200 dark:border-surface-700 border rounded-lg p-4">
        <h2 class="text-lg font-semibold mb-4">Most common words using this kanji</h2>
        <div class="space-y-1">
          <KanjiWordRow v-for="word in visibleTopWords" :key="`${word.wordId}-${word.readingIndex}`" :word="word" />
        </div>
        <div class="mt-2">
          <button
            v-if="!allTopWords"
            class="text-sm text-primary-600 dark:text-primary-400 hover:underline cursor-pointer"
            :disabled="loadingTopWords"
            @click="loadAllTopWords"
          >
            {{ loadingTopWords ? 'Loading...' : 'View all' }}
          </button>
          <button v-else class="text-sm text-primary-600 dark:text-primary-400 hover:underline cursor-pointer" @click="collapseTopWords">View less</button>
        </div>
      </div>
    </div>
  </div>
</template>
