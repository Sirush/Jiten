<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import MultiSelect from 'primevue/multiselect';
  import Slider from 'primevue/slider';
  import InputNumber from 'primevue/inputnumber';
  import ProgressSpinner from 'primevue/progressspinner';
  import DataTable from 'primevue/datatable';
  import Column from 'primevue/column';
  import { Bar, Line } from 'vue-chartjs';
  import { Chart as ChartJS, CategoryScale, LinearScale, BarElement, LineElement, PointElement, Tooltip, Legend } from 'chart.js';
  import { MediaType } from '~/types/enums';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import type {
    CorpusSnippet,
    CorpusMediaBreakdown,
    CorpusTrendPoint,
    CorpusDifficultyBucket,
    CorpusTopDeck,
    CorpusTermResult,
    CorpusStats,
    CorpusSearchResponse,
    CorpusCoOccurrence,
  } from '~/types/types';

  ChartJS.register(CategoryScale, LinearScale, BarElement, LineElement, PointElement, Tooltip, Legend);

  useHead({
    title: 'Corpus Analysis - Jiten Admin',
  });

  definePageMeta({
    middleware: ['auth'],
  });

  const { $api } = useNuxtApp();

  const terms = ref<string[]>(['']);
  const selectedMediaTypes = ref<MediaType[]>([]);
  const difficultyRange = ref<[number, number]>([0, 5]);
  const minYear = ref<number | null>(null);
  const maxYear = ref<number | null>(null);
  const maxSnippets = ref(50);
  const loading = ref(false);
  const exporting = ref(false);
  const publishing = ref(false);
  const publishedUrl = ref('');
  const publishedUrlCopied = ref(false);
  const searchResponse = ref<CorpusSearchResponse | null>(null);
  const coOccurrences = ref<CorpusCoOccurrence[]>([]);
  const errorMsg = ref('');
  const activeTermIndex = ref(0);

  const mediaTypeOptions = Object.values(MediaType)
    .filter((v) => typeof v === 'number')
    .map((v) => ({ label: getMediaTypeText(v as MediaType), value: v }));

  const chartColors = ['#bd93f9', '#50fa7b', '#ff79c6', '#8be9fd', '#f1fa8c'];

  const termInputs = ref<any[]>([]);
  const TERM_SEPARATORS = /[,;/]/;

  function setTermRef(el: any, i: number) {
    if (el) termInputs.value[i] = el;
  }

  // Typing or pasting a separator (, ; /) splits the term into additional fields.
  // The DOM value must be reset imperatively: Vue coalesces "test"→"test,"→"test"
  // into one render, diffs against the last *rendered* value ("test"), sees no change,
  // and never patches the input — so the typed separator stays in the DOM otherwise.
  watch(
    terms,
    (newTerms) => {
      for (let i = 0; i < newTerms.length; i++) {
        if (!TERM_SEPARATORS.test(newTerms[i])) continue;

        const parts = newTerms[i].split(TERM_SEPARATORS).map((p) => p.trim());
        terms.value[i] = parts[0];

        let insertAt = i + 1;
        for (let k = 1; k < parts.length && terms.value.length < 10; k++) {
          terms.value.splice(insertAt, 0, parts[k]);
          insertAt++;
        }

        const splitIndex = i;
        const focusIndex = insertAt - 1;
        nextTick(() => {
          const splitEl = termInputs.value[splitIndex]?.$el as HTMLInputElement | undefined;
          if (splitEl) splitEl.value = parts[0];
          termInputs.value[focusIndex]?.$el?.focus();
        });
        break; // one split per pass; the mutation re-triggers this watcher
      }
    },
    { deep: true },
  );

  function addTerm() {
    if (terms.value.length < 10) terms.value.push('');
  }

  function removeTerm(index: number) {
    terms.value.splice(index, 1);
    termInputs.value.splice(index, 1);
    if (activeTermIndex.value >= terms.value.length) activeTermIndex.value = Math.max(0, terms.value.length - 1);
  }

  function buildRequest() {
    const validTerms = terms.value.filter((t) => t.trim().length > 0);
    return {
      terms: validTerms,
      mediaTypes: selectedMediaTypes.value.length > 0 ? selectedMediaTypes.value : undefined,
      minDifficulty: difficultyRange.value[0] > 0 ? difficultyRange.value[0] : undefined,
      maxDifficulty: difficultyRange.value[1] < 5 ? difficultyRange.value[1] : undefined,
      minReleaseYear: minYear.value ?? undefined,
      maxReleaseYear: maxYear.value ?? undefined,
      maxSnippets: maxSnippets.value,
    };
  }

  async function search() {
    const req = buildRequest();
    if (req.terms.length === 0) return;

    loading.value = true;
    errorMsg.value = '';
    searchResponse.value = null;
    coOccurrences.value = [];
    publishedUrl.value = '';

    try {
      const [searchResult, coOccResult] = await Promise.all([
        $api<CorpusSearchResponse>('corpus/search', { method: 'POST', body: req }),
        req.terms.length > 1 ? $api<CorpusCoOccurrence[]>('corpus/co-occurrences', { method: 'POST', body: req }) : Promise.resolve([]),
      ]);
      searchResponse.value = searchResult;
      coOccurrences.value = coOccResult;

      activeTermIndex.value = searchResponse.value!.results.length > 1 ? -1 : 0;
    } catch (e: any) {
      errorMsg.value = e?.data?.message || e?.message || 'Search failed';
    } finally {
      loading.value = false;
    }
  }

  async function exportHtml() {
    const req = buildRequest();
    if (req.terms.length === 0) return;

    exporting.value = true;
    try {
      const blob = await $api<Blob>('corpus/export', {
        method: 'POST',
        body: req,
        responseType: 'blob',
      });

      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `corpus-report-${Date.now()}.html`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (e: any) {
      errorMsg.value = e?.data?.message || e?.message || 'Export failed';
    } finally {
      exporting.value = false;
    }
  }

  async function publishReport() {
    const req = buildRequest();
    if (req.terms.length === 0) return;

    publishing.value = true;
    publishedUrl.value = '';
    errorMsg.value = '';
    try {
      const res = await $api<{ url: string }>('corpus/publish', { method: 'POST', body: req });
      publishedUrl.value = res.url;
    } catch (e: any) {
      errorMsg.value = e?.data?.message || e?.message || 'Publish failed';
    } finally {
      publishing.value = false;
    }
  }

  function copyPublishedUrl() {
    if (!publishedUrl.value) return;
    void navigator.clipboard?.writeText(publishedUrl.value).then(() => {
      publishedUrlCopied.value = true;
      setTimeout(() => (publishedUrlCopied.value = false), 2000);
    });
  }

  const activeResult = computed(() => {
    if (!searchResponse.value) return null;
    return searchResponse.value.results[activeTermIndex.value] ?? null;
  });

  const defaultBarOptions = {
    responsive: true,
    maintainAspectRatio: true,
    aspectRatio: 2,
    indexAxis: 'y' as const,
    interaction: { mode: 'index' as const, intersect: false },
    plugins: {
      legend: { display: false },
      datalabels: { display: false },
      tooltip: {
        backgroundColor: 'rgba(0,0,0,0.85)',
        titleColor: '#fff',
        bodyColor: '#fff',
        borderColor: '#bd93f9',
        borderWidth: 1,
        padding: 10,
      },
    },
    scales: {
      x: {
        grid: { color: 'rgba(128,128,128,0.2)' },
        ticks: { color: '#888' },
      },
      y: {
        grid: { display: false },
        ticks: { color: '#888', font: { size: 12 } },
      },
    },
  };

  const mediaChartData = computed(() => {
    if (!activeResult.value) return null;
    const sorted = [...activeResult.value.mediaBreakdown].sort((a, b) => a.hitsPerMillion - b.hitsPerMillion);
    return {
      labels: sorted.map((m) => getMediaTypeText(m.mediaType)),
      datasets: [
        {
          label: 'Occ/M chars',
          data: sorted.map((m) => +m.hitsPerMillion.toFixed(1)),
          backgroundColor: '#bd93f9',
          borderRadius: 4,
        },
      ],
    };
  });

  const difficultyChartData = computed(() => {
    if (!activeResult.value) return null;
    const buckets = activeResult.value.difficultyDistribution;
    return {
      labels: buckets.map((d) => `${d.bucketMin}–${d.bucketMax}`),
      datasets: [
        {
          label: 'Matching Decks',
          data: buckets.map((d) => d.deckCount),
          backgroundColor: '#50fa7b',
          borderRadius: 4,
        },
      ],
    };
  });

  const difficultyBarOptions = {
    responsive: true,
    maintainAspectRatio: true,
    aspectRatio: 2.5,
    interaction: { mode: 'index' as const, intersect: false },
    plugins: {
      legend: { display: false },
      datalabels: { display: false },
      tooltip: {
        backgroundColor: 'rgba(0,0,0,0.85)',
        titleColor: '#fff',
        bodyColor: '#fff',
        borderColor: '#50fa7b',
        borderWidth: 1,
        padding: 10,
      },
    },
    scales: {
      x: {
        title: { display: true, text: 'Difficulty', color: '#888' },
        grid: { display: false },
        ticks: { color: '#888' },
      },
      y: {
        title: { display: true, text: 'Decks', color: '#888' },
        grid: { color: 'rgba(128,128,128,0.2)' },
        ticks: { color: '#888' },
      },
    },
  };

  const isAllView = computed(() => activeTermIndex.value === -1);

  const trendChartData = computed(() => {
    if (!activeResult.value) return null;
    const trends = activeResult.value.trends;
    return {
      labels: trends.map((t) => t.year.toString()),
      datasets: [
        {
          label: activeResult.value.term,
          data: trends.map((t) => +t.percentage.toFixed(2)),
          borderColor: '#ff79c6',
          backgroundColor: 'rgba(255,121,198,0.15)',
          fill: true,
          tension: 0.3,
          pointRadius: 3,
          pointBackgroundColor: '#ff79c6',
        },
      ],
    };
  });

  const combinedTrendChartData = computed(() => {
    if (!searchResponse.value || searchResponse.value.results.length < 2) return null;

    const allYears = new Set<string>();
    for (const r of searchResponse.value.results) {
      for (const t of r.trends) allYears.add(t.year.toString());
    }
    const labels = [...allYears].sort();

    const datasets = searchResponse.value.results.map((r, i) => {
      const yearMap = new Map(r.trends.map((t) => [t.year.toString(), t.percentage]));
      return {
        label: r.term,
        data: labels.map((y) => +(yearMap.get(y) ?? 0).toFixed(2)),
        borderColor: chartColors[i % chartColors.length],
        backgroundColor: 'transparent',
        fill: false,
        tension: 0.3,
        pointRadius: 3,
        pointBackgroundColor: chartColors[i % chartColors.length],
      };
    });

    return { labels, datasets };
  });

  const trendLineOptions = computed(() => ({
    responsive: true,
    maintainAspectRatio: true,
    aspectRatio: 3,
    interaction: { mode: 'index' as const, intersect: false },
    plugins: {
      legend: { display: isAllView.value, labels: { color: '#888' } },
      datalabels: { display: false },
      tooltip: {
        backgroundColor: 'rgba(0,0,0,0.85)',
        titleColor: '#fff',
        bodyColor: '#fff',
        borderColor: '#ff79c6',
        borderWidth: 1,
        padding: 10,
      },
    },
    scales: {
      x: {
        title: { display: true, text: 'Release Year', color: '#888' },
        grid: { color: 'rgba(128,128,128,0.2)' },
        ticks: { color: '#888', maxRotation: 45 },
      },
      y: {
        title: { display: true, text: 'Per million chars', color: '#888' },
        grid: { color: 'rgba(128,128,128,0.2)' },
        ticks: { color: '#888' },
        beginAtZero: true,
      },
    },
  }));

  // --- Concordance: KWIC sorting + citation export ---
  const kwicSort = ref<'none' | 'left' | 'right'>('none');

  function stripTags(s: string): string {
    return s.replace(/<[^>]*>/g, '');
  }

  function kwicParts(html: string): { left: string; right: string } {
    const m = html.match(/<span class="keyword">[\s\S]*?<\/span>/);
    if (!m || m.index === undefined) return { left: stripTags(html), right: '' };
    return { left: stripTags(html.slice(0, m.index)), right: stripTags(html.slice(m.index + m[0].length)) };
  }

  // KWIC sorting relies on the highlighted match span; guard against snippets without one.
  const kwicAvailable = computed(() => activeResult.value?.snippets.some((s) => s.html.includes('class="keyword"')) ?? false);

  const sortedSnippets = computed(() => {
    const list = activeResult.value?.snippets ?? [];
    if (kwicSort.value === 'none' || !kwicAvailable.value) return list;
    const decorated = list.map((s) => ({ s, parts: kwicParts(s.html) }));
    decorated.sort((a, b) => {
      if (kwicSort.value === 'right') return a.parts.right.localeCompare(b.parts.right, 'ja');
      // left sort: order by the context reading backwards from the keyword
      const al = [...a.parts.left].reverse().join('');
      const bl = [...b.parts.left].reverse().join('');
      return al.localeCompare(bl, 'ja');
    });
    return decorated.map((d) => d.s);
  });

  const comparisonCopied = ref(false);

  const matchingDecksByTerm = computed(() => {
    const map = new Map<string, number>();
    for (const r of searchResponse.value?.results ?? []) map.set(r.term, r.matchingDecks);
    return map;
  });

  // Overlap coefficient: shared decks as a share of the rarer term's matching decks.
  function coOccurrenceOverlap(c: CorpusCoOccurrence): number {
    const a = matchingDecksByTerm.value.get(c.termA) ?? 0;
    const b = matchingDecksByTerm.value.get(c.termB) ?? 0;
    const denom = Math.min(a, b);
    return denom > 0 ? (c.sharedDecks / denom) * 100 : 0;
  }

  // Jaccard: shared decks as a share of the union (decks containing either term).
  function coOccurrenceJaccard(c: CorpusCoOccurrence): number {
    const a = matchingDecksByTerm.value.get(c.termA) ?? 0;
    const b = matchingDecksByTerm.value.get(c.termB) ?? 0;
    const union = a + b - c.sharedDecks;
    return union > 0 ? (c.sharedDecks / union) * 100 : 0;
  }

  // Markdown table for pasting into JMdict-style comment sections.
  // "Share %" is each term's portion of the combined occurrences across all terms.
  function copyComparison() {
    const results = searchResponse.value?.results;
    if (!results || results.length === 0) return;

    const totalOcc = results.reduce((sum, r) => sum + r.totalOccurrences, 0);
    const header = '| Word | Occurrences | Work range % | Share % |';
    const divider = '| --- | ---: | ---: | ---: |';
    const rows = results.map((r) => {
      const share = totalOcc > 0 ? (r.totalOccurrences / totalOcc) * 100 : 0;
      return `| ${r.term} | ${r.totalOccurrences.toLocaleString()} | ${r.workRangePercentage.toFixed(1)}% | ${share.toFixed(1)}% |`;
    });
    const table = [header, divider, ...rows].join('\n');

    void navigator.clipboard?.writeText(table).then(() => {
      comparisonCopied.value = true;
      setTimeout(() => (comparisonCopied.value = false), 2000);
    });
  }

  function copyCitation(s: CorpusSnippet) {
    const src = s.parentTitle ? `${s.parentTitle} — ${s.deckTitle}` : s.deckTitle;
    const year = s.releaseYear ? ` (${s.releaseYear})` : '';
    void navigator.clipboard?.writeText(`${s.text}　【${src}${year}】`);
  }

  function downloadCitations() {
    const r = activeResult.value;
    if (!r) return;
    const header = ['term', 'work', 'subdeck', 'year', 'mediaType', 'difficulty', 'text'];
    const rows = r.snippets.map((s) => [
      r.term,
      s.parentTitle ?? s.deckTitle,
      s.parentTitle ? s.deckTitle : '',
      s.releaseYear || '',
      getMediaTypeText(s.mediaType),
      s.difficulty.toFixed(1),
      s.text.replace(/[\t\r\n]+/g, ' '),
    ]);
    const tsv = [header, ...rows].map((cols) => cols.join('\t')).join('\n');
    const blob = new Blob([tsv], { type: 'text/tab-separated-values;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `corpus-citations-${r.term}.tsv`;
    a.click();
    URL.revokeObjectURL(url);
  }
</script>

<template>
  <div class="mx-auto max-w-[1400px] p-4">
    <h1 class="mb-2 text-2xl font-bold text-surface-900 dark:text-surface-0">Corpus Analysis</h1>

    <details
      class="mb-4 rounded border border-surface-200 bg-surface-50 p-3 text-sm text-surface-600 dark:border-surface-700 dark:bg-surface-900/40 dark:text-surface-300"
    >
      <summary class="cursor-pointer font-semibold text-surface-700 dark:text-surface-200">Search syntax & tips</summary>
      <ul class="mt-2 list-disc space-y-1 pl-5">
        <li>
          Phrase search (PGroonga <code>&amp;@</code>): the box is <strong>one keyword</strong> matched against the bigram index — it finds that exact run of
          characters anywhere in the text, e.g. <code>について</code>, <code>かもしれない</code>.
        </li>
        <li>
          <strong>No operators:</strong> spaces, <code>AND</code>/<code>OR</code>, <code>*</code>, <code>"…"</code>, regex and SQL <code>%</code> are matched
          literally, not interpreted — so don't use them.
        </li>
        <li>
          Latin letters, digits and symbols are tokenised <strong>separately</strong> from kana/kanji, and 1-character queries are imprecise (bigrams need ≥2
          chars).
        </li>
        <li>
          <strong>Not lemmatised</strong> — inflected forms are separate strings (食べた ≠ 食べる). Enter each form you care about as its own term (up to 10) to
          count and compare them side by side, e.g. <code>食べた</code> · <code>食べて</code> · <code>食べます</code>.
        </li>
        <li>Inline furigana <code>{漢字'かんじ}</code> is stripped before matching: searching <code>漢字</code> matches and the reading is ignored.</li>
        <li>
          <strong>Matching Decks vs Works (range):</strong> a deck is a single entry, but long media is split into sub-decks (chapters / episodes /
          volumes). <strong>Matching Decks</strong> counts every sub-deck; <strong>Works (range)</strong> collapses them to the parent title — so 500 hits
          across one 30-chapter novel are 30 decks but 1 work. Works (range) is the honest "how many distinct titles use this" signal.
        </li>
        <li>
          <strong>Dispersion</strong> (Gries' Deviation of Proportions) measures how evenly the term is spread across media types relative to each
          register's size: <strong>0</strong> = used everywhere in proportion to corpus size, <strong>1</strong> = concentrated in a single register. A
          common word with low dispersion is register-specific (e.g. slang mostly in subtitles).
        </li>
      </ul>
    </details>

    <Card class="mb-4">
      <template #content>
        <div class="flex flex-col gap-5">
          <div class="flex flex-col gap-2">
            <label class="text-xs font-medium text-surface-500">Search terms (up to 10)</label>
            <span class="text-xs text-surface-400">Quickly add a new term by typing or pasting with these separators <code>,</code> <code>;</code> <code>/</code></span>
            <div v-for="(_, i) in terms" :key="i" class="flex items-center gap-2">
              <InputText
                :ref="(el) => setTermRef(el, i)"
                v-model="terms[i]"
                :placeholder="`Term ${i + 1}`"
                class="w-full max-w-md"
                @keydown.enter="search"
              />
              <Button v-if="terms.length > 1" icon="pi pi-times" severity="danger" text rounded size="small" @click="removeTerm(i)" />
            </div>
            <div>
              <Button v-if="terms.length < 10" label="Add term" icon="pi pi-plus" severity="secondary" size="small" @click="addTerm" />
            </div>
          </div>

          <div class="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
            <div class="flex flex-col gap-1.5">
              <label class="text-xs font-medium text-surface-500">Media types</label>
              <MultiSelect v-model="selectedMediaTypes" :options="mediaTypeOptions" option-label="label" option-value="value" placeholder="All types" fluid />
            </div>

            <div class="flex flex-col gap-1.5">
              <label class="text-xs font-medium text-surface-500">Max snippets</label>
              <InputNumber v-model="maxSnippets" :min="1" :max="50" show-buttons fluid />
            </div>

            <div class="flex flex-col gap-1.5">
              <label class="text-xs font-medium text-surface-500">Difficulty: {{ difficultyRange[0] }}–{{ difficultyRange[1] }}</label>
              <div class="px-2 py-3">
                <Slider v-model="difficultyRange" :min="0" :max="5" :step="0.5" range />
              </div>
            </div>

            <div class="flex flex-col gap-1.5 sm:col-span-2">
              <label class="text-xs font-medium text-surface-500">Release year range</label>
              <div class="flex items-center gap-2">
                <InputNumber v-model="minYear" placeholder="From" :use-grouping="false" fluid />
                <span class="text-surface-400">–</span>
                <InputNumber v-model="maxYear" placeholder="To" :use-grouping="false" fluid />
              </div>
            </div>
          </div>

          <div class="flex flex-wrap gap-2 border-t border-surface-200 pt-4 dark:border-surface-700">
            <Button label="Search" icon="pi pi-search" :loading="loading" @click="search" />
            <Button label="Export HTML" icon="pi pi-download" severity="secondary" :loading="exporting" :disabled="!searchResponse" @click="exportHtml" />
            <Button
              label="Publish & get link"
              icon="pi pi-cloud-upload"
              severity="secondary"
              :loading="publishing"
              :disabled="!searchResponse"
              @click="publishReport"
            />
          </div>

          <div
            v-if="publishedUrl"
            class="flex flex-wrap items-center gap-2 rounded border border-surface-200 bg-surface-50 p-2 dark:border-surface-700 dark:bg-surface-900/40"
          >
            <span class="text-xs font-medium text-surface-500">Published report:</span>
            <a :href="publishedUrl" target="_blank" rel="noopener" class="flex-1 truncate text-sm text-primary-400 hover:underline">{{ publishedUrl }}</a>
            <Button
              :label="publishedUrlCopied ? 'Copied!' : 'Copy link'"
              :icon="publishedUrlCopied ? 'pi pi-check' : 'pi pi-copy'"
              size="small"
              @click="copyPublishedUrl"
            />
          </div>
        </div>
      </template>
    </Card>

    <div v-if="errorMsg" class="mb-4 rounded bg-red-100 p-3 text-red-700 dark:bg-red-900/50 dark:text-red-200">{{ errorMsg }}</div>

    <div v-if="loading" class="flex justify-center py-12">
      <ProgressSpinner />
    </div>

    <template v-if="searchResponse && !loading">
      <div
        class="mb-4 rounded border border-surface-200 bg-surface-50 p-3 text-sm dark:border-surface-700 dark:bg-surface-900/40"
      >
        <div class="mb-1 flex flex-wrap items-baseline gap-x-5 gap-y-1">
          <span class="text-xs font-semibold uppercase tracking-wide text-surface-500">
            {{ searchResponse.filteredScope.hasFilters ? 'Filtered scope' : 'Searchable corpus' }}
          </span>
          <span class="text-surface-700 dark:text-surface-200"
            ><strong>{{ searchResponse.filteredScope.decks.toLocaleString() }}</strong> decks</span
          >
          <span class="text-surface-700 dark:text-surface-200"
            ><strong>{{ searchResponse.filteredScope.works.toLocaleString() }}</strong> works</span
          >
          <span class="text-surface-700 dark:text-surface-200"
            ><strong>{{ searchResponse.filteredScope.characters.toLocaleString() }}</strong> characters</span
          >
        </div>
        <p v-if="searchResponse.filteredScope.hasFilters" class="text-xs text-surface-400">
          Narrowed by your filters (year / difficulty / media type) from
          {{ searchResponse.corpusStats.decksWithRawText.toLocaleString() }} decks ·
          {{ searchResponse.corpusStats.totalWorks.toLocaleString() }} works ·
          {{ searchResponse.corpusStats.totalCharacters.toLocaleString() }} characters total. All stats below are relative to this scope.
        </p>
      </div>

      <Card v-if="searchResponse.results.length > 1" class="mb-4">
        <template #title>
          <div class="flex flex-wrap items-center justify-between gap-2">
            <span>Comparison Overview</span>
            <Button
              :label="comparisonCopied ? 'Copied!' : 'Copy table'"
              :icon="comparisonCopied ? 'pi pi-check' : 'pi pi-copy'"
              size="small"
              title="Copy as a Markdown table (word, occurrences, work range %, share %)"
              @click="copyComparison"
            />
          </div>
        </template>
        <template #content>
          <DataTable :value="searchResponse.results" size="small" striped-rows>
            <Column header="Term">
              <template #body="{ data, index }">
                <a class="cursor-pointer font-bold text-primary-400 hover:underline" @click="activeTermIndex = index">{{ data.term }}</a>
              </template>
            </Column>
            <Column field="matchingDecks" header="Matching Decks" />
            <Column header="Occurrences">
              <template #body="{ data }">{{ data.totalOccurrences.toLocaleString() }}</template>
            </Column>
            <Column header="Occ/M chars">
              <template #body="{ data }">{{ data.hitsPerMillion.toFixed(1) }}</template>
            </Column>
            <Column header="Works (range)">
              <template #body="{ data }">{{ data.worksMatched.toLocaleString() }} ({{ data.workRangePercentage.toFixed(1) }}%)</template>
            </Column>
            <Column header="Dispersion" title="Gries' Deviation of Proportions across media types: 0 = spread evenly across registers, 1 = concentrated in one">
              <template #body="{ data }">{{ data.dispersion.toFixed(2) }}</template>
            </Column>
            <Column header="Dialogue %">
              <template #body="{ data }">{{ data.dialogueWeightedAvg.toFixed(1) }}%</template>
            </Column>
          </DataTable>

          <p class="mt-2 text-xs text-surface-500">
            <strong>Matching Decks</strong> counts every individual deck, including sub-decks (each chapter / episode / volume).
            <strong>Works (range)</strong> collapses those sub-decks to their parent title, so a term in all chapters of one novel counts as many decks
            but a single work — the % is the share of all works in the corpus.
          </p>
          <p class="mt-1 text-xs text-surface-500">
            <strong>Dispersion</strong> (Gries' Deviation of Proportions) measures how evenly the term is spread across media types relative to each
            register's size: <strong>0</strong> = used in proportion everywhere, <strong>1</strong> = concentrated in a single register (e.g. only in
            subtitles or only in novels).
          </p>

          <div v-if="coOccurrences.length > 0" class="mt-4">
            <h3 class="mb-2 text-sm font-semibold text-surface-600 dark:text-surface-300">Co-occurrence (shared decks)</h3>
            <DataTable :value="coOccurrences" size="small" striped-rows>
              <Column field="termA" header="Term A" />
              <Column field="termB" header="Term B" />
              <Column field="sharedDecks" header="Shared Decks" />
              <Column header="Overlap %" title="Shared decks as a share of the rarer term's matching decks (shared / min)">
                <template #body="{ data }">{{ coOccurrenceOverlap(data).toFixed(1) }}%</template>
              </Column>
              <Column header="Jaccard %" title="Shared decks as a share of the decks containing either term (shared / union)">
                <template #body="{ data }">{{ coOccurrenceJaccard(data).toFixed(1) }}%</template>
              </Column>
            </DataTable>

            <p class="mt-2 text-xs text-surface-500">
              <strong>Shared Decks</strong> is the number of decks containing both terms (deck-level, like Matching Decks).
              <strong>Overlap %</strong> = shared ÷ the rarer term's decks — "what share of the less common term's decks also contain the other"
              (reaches 100% if one term's decks are a subset of the other's).
              <strong>Jaccard %</strong> = shared ÷ decks containing either term — symmetric, but pulled low when one term is far more common.
            </p>
          </div>
        </template>
      </Card>

      <div v-if="searchResponse.results.length > 1" class="mb-4 flex gap-2">
        <Button label="All" :severity="activeTermIndex === -1 ? undefined : 'secondary'" size="small" @click="activeTermIndex = -1" />
        <Button
          v-for="(r, i) in searchResponse.results"
          :key="i"
          :label="r.term"
          :severity="activeTermIndex === i ? undefined : 'secondary'"
          size="small"
          @click="activeTermIndex = i"
        />
      </div>

      <template v-if="isAllView && combinedTrendChartData">
        <Card class="mb-4">
          <template #title>Combined Trends</template>
          <template #content>
            <h3 class="mb-2 font-semibold text-surface-700 dark:text-surface-200">Temporal Trends (Frequency by Release Year (per million chars))</h3>
            <Line :data="combinedTrendChartData" :options="trendLineOptions" />
          </template>
        </Card>
      </template>

      <template v-if="!isAllView && activeResult">
        <Card class="mb-4">
          <template #title>{{ activeResult.term }}</template>
          <template #subtitle>
            {{ activeResult.totalOccurrences.toLocaleString() }} occurrences · {{ activeResult.hitsPerMillion.toFixed(1) }} occ/M chars · in
            {{ activeResult.worksMatched.toLocaleString() }}/{{ activeResult.worksTotal.toLocaleString() }} works ({{
              activeResult.workRangePercentage.toFixed(1)
            }}%) · dispersion {{ activeResult.dispersion.toFixed(2) }} · Avg dialogue: {{ activeResult.dialogueWeightedAvg.toFixed(1) }}%
          </template>
          <template #content>
            <div class="grid grid-cols-1 gap-6 lg:grid-cols-2">
              <div v-if="mediaChartData">
                <h3 class="mb-2 font-semibold text-surface-700 dark:text-surface-200">Media Type Breakdown (occ/M chars)</h3>
                <Bar :data="mediaChartData" :options="defaultBarOptions" />
              </div>

              <div v-if="difficultyChartData">
                <h3 class="mb-2 font-semibold text-surface-700 dark:text-surface-200">Difficulty Distribution</h3>
                <Bar :data="difficultyChartData" :options="difficultyBarOptions" />
              </div>
            </div>

            <div v-if="trendChartData" class="mt-6">
              <h3 class="mb-2 font-semibold text-surface-700 dark:text-surface-200">Temporal Trends (Frequency by Release Year (per million chars))</h3>
              <Line :data="trendChartData" :options="trendLineOptions" />
            </div>
          </template>
        </Card>

        <Card v-if="activeResult.topDecks?.length > 0" class="mb-4">
          <template #title>Top 20 Decks by Occurrences</template>
          <template #content>
            <DataTable :value="activeResult.topDecks" size="small" striped-rows>
              <Column header="#" style="width: 3rem">
                <template #body="{ index }">{{ index + 1 }}</template>
              </Column>
              <Column header="Source">
                <template #body="{ data }">
                  <NuxtLink :to="`/media/${data.deckId}`" class="text-primary-400 hover:underline">
                    <template v-if="data.parentTitle">{{ data.parentTitle }} — </template>{{ data.title }}
                  </NuxtLink>
                </template>
              </Column>
              <Column header="Type" style="width: 7rem">
                <template #body="{ data }">{{ getMediaTypeText(data.mediaType) }}</template>
              </Column>
              <Column header="Occurrences" style="width: 7rem">
                <template #body="{ data }">{{ data.occurrences.toLocaleString() }}</template>
              </Column>
              <Column header="Per M chars" style="width: 7rem">
                <template #body="{ data }">{{ data.perMillion.toFixed(1) }}</template>
              </Column>
            </DataTable>
          </template>
        </Card>

        <Card v-if="activeResult.snippets.length > 0">
          <template #title>
            <div class="flex flex-wrap items-center justify-between gap-2">
              <span>Concordance ({{ activeResult.snippets.length }} citations · one per work)</span>
              <div class="flex items-center gap-2">
                <template v-if="kwicAvailable">
                  <span class="text-xs text-surface-500" title="Reorder citations by the context around the match, so recurring patterns line up"
                    >Sort by context:</span
                  >
                  <Button
                    label="Off"
                    size="small"
                    title="Original order"
                    :severity="kwicSort === 'none' ? undefined : 'secondary'"
                    @click="kwicSort = 'none'"
                  />
                  <Button
                    label="Before"
                    size="small"
                    title="Sort by the words immediately before the match (left context)"
                    :severity="kwicSort === 'left' ? undefined : 'secondary'"
                    @click="kwicSort = 'left'"
                  />
                  <Button
                    label="After"
                    size="small"
                    title="Sort by the words immediately after the match (right context)"
                    :severity="kwicSort === 'right' ? undefined : 'secondary'"
                    @click="kwicSort = 'right'"
                  />
                </template>
                <Button label="Export TSV" icon="pi pi-download" size="small" severity="secondary" @click="downloadCitations" />
              </div>
            </div>
          </template>
          <template #content>
            <DataTable :value="sortedSnippets" size="small" striped-rows paginator :rows="20">
              <Column header="#" style="width: 3rem">
                <template #body="{ index }">{{ index + 1 }}</template>
              </Column>
              <Column header="Context" style="min-width: 400px">
                <template #body="{ data }">
                  <!-- eslint-disable-next-line vue/no-v-html -->
                  <span class="snippet-text" v-html="sanitiseHtml(data.html)" />
                </template>
              </Column>
              <Column header="Source">
                <template #body="{ data }">
                  <NuxtLink :to="`/media/${data.deckId}`" class="text-primary-400 hover:underline">
                    <template v-if="data.parentTitle">{{ data.parentTitle }} — </template>{{ data.deckTitle }}
                  </NuxtLink>
                </template>
              </Column>
              <Column header="Type">
                <template #body="{ data }">{{ getMediaTypeText(data.mediaType) }}</template>
              </Column>
              <Column header="Diff" style="width: 4rem">
                <template #body="{ data }">{{ data.difficulty.toFixed(1) }}</template>
              </Column>
              <Column field="releaseYear" header="Year" style="width: 4rem" />
              <Column header="" style="width: 3rem">
                <template #body="{ data }">
                  <Button icon="pi pi-copy" text rounded size="small" title="Copy citation" @click="copyCitation(data)" />
                </template>
              </Column>
            </DataTable>
          </template>
        </Card>
      </template>
    </template>
  </div>
</template>

<style scoped>
  :deep(.snippet-text .keyword) {
    color: var(--p-primary-color);
    font-weight: 700;
  }

  details code {
    background: var(--p-surface-200);
    color: var(--p-surface-800);
    padding: 0 4px;
    border-radius: 3px;
    font-size: 0.85em;
  }

  :global(.dark-mode) details code {
    background: var(--p-surface-700);
    color: var(--p-surface-100);
  }
</style>
