<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import MultiSelect from 'primevue/multiselect';
  import Slider from 'primevue/slider';
  import InputNumber from 'primevue/inputnumber';
  import ToggleSwitch from 'primevue/toggleswitch';
  import ProgressSpinner from 'primevue/progressspinner';
  import DataTable from 'primevue/datatable';
  import Column from 'primevue/column';
  import { Bar, Line } from 'vue-chartjs';
  import {
    Chart as ChartJS,
    CategoryScale,
    LinearScale,
    BarElement,
    LineElement,
    PointElement,
    Tooltip,
    Legend,
  } from 'chart.js';
  import { MediaType } from '~/types/enums';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import type { CorpusSnippet, CorpusMediaBreakdown, CorpusTrendPoint, CorpusDifficultyBucket, CorpusTopDeck, CorpusTermResult, CorpusStats, CorpusSearchResponse, CorpusCoOccurrence } from '~/types/types';

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
  const useRegex = ref(false);
  const maxSnippets = ref(50);
  const loading = ref(false);
  const exporting = ref(false);
  const searchResponse = ref<CorpusSearchResponse | null>(null);
  const coOccurrences = ref<CorpusCoOccurrence[]>([]);
  const errorMsg = ref('');
  const activeTermIndex = ref(0);

  const mediaTypeOptions = Object.values(MediaType)
    .filter((v) => typeof v === 'number')
    .map((v) => ({ label: getMediaTypeText(v as MediaType), value: v }));

  const chartColors = ['#bd93f9', '#50fa7b', '#ff79c6', '#8be9fd', '#f1fa8c'];

  function addTerm() {
    if (terms.value.length < 5) terms.value.push('');
  }

  function removeTerm(index: number) {
    terms.value.splice(index, 1);
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
      useRegex: useRegex.value,
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

    try {
      const [searchResult, coOccResult] = await Promise.all([
        $api<CorpusSearchResponse>('corpus/search', { method: 'POST', body: req }),
        req.terms.length > 1
          ? $api<CorpusCoOccurrence[]>('corpus/co-occurrences', { method: 'POST', body: req })
          : Promise.resolve([]),
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

  const activeResult = computed(() => {
    if (!searchResponse.value) return null;
    return searchResponse.value.results[activeTermIndex.value] ?? null;
  });

  const defaultBarOptions = {
    responsive: true,
    maintainAspectRatio: true,
    aspectRatio: 2,
    indexAxis: 'y' as const,
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
        grid: { color: 'rgba(255,255,255,0.06)' },
        ticks: { color: '#aaa' },
      },
      y: {
        grid: { display: false },
        ticks: { color: '#ddd', font: { size: 12 } },
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
        title: { display: true, text: 'Difficulty', color: '#aaa' },
        grid: { display: false },
        ticks: { color: '#ddd' },
      },
      y: {
        title: { display: true, text: 'Decks', color: '#aaa' },
        grid: { color: 'rgba(255,255,255,0.06)' },
        ticks: { color: '#aaa' },
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
    plugins: {
      legend: { display: isAllView.value, labels: { color: '#ddd' } },
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
        title: { display: true, text: 'Release Year', color: '#aaa' },
        grid: { color: 'rgba(255,255,255,0.06)' },
        ticks: { color: '#ddd', maxRotation: 45 },
      },
      y: {
        title: { display: true, text: 'Per million chars', color: '#aaa' },
        grid: { color: 'rgba(255,255,255,0.06)' },
        ticks: { color: '#aaa' },
        beginAtZero: true,
      },
    },
  }));
</script>

<template>
  <div class="mx-auto max-w-[1400px] p-4">
    <h1 class="mb-4 text-2xl font-bold text-surface-0">Corpus Analysis</h1>

    <Card class="mb-4">
      <template #content>
        <div class="flex flex-col gap-4">
          <div v-for="(_, i) in terms" :key="i" class="flex items-center gap-2">
            <InputText v-model="terms[i]" :placeholder="`Search term ${i + 1}`" class="w-full max-w-md" @keydown.enter="search" />
            <Button v-if="terms.length > 1" icon="pi pi-times" severity="danger" text rounded size="small" @click="removeTerm(i)" />
          </div>
          <div class="flex flex-wrap items-center gap-3">
            <Button v-if="terms.length < 5" label="Add Term" icon="pi pi-plus" severity="secondary" size="small" @click="addTerm" />
            <MultiSelect v-model="selectedMediaTypes" :options="mediaTypeOptions" option-label="label" option-value="value" placeholder="Media Types" class="w-60" />
            <div class="flex items-center gap-2">
              <span class="text-sm">Difficulty:</span>
              <Slider v-model="difficultyRange" :min="0" :max="5" :step="0.5" range class="w-40" />
              <span class="text-xs text-surface-400">{{ difficultyRange[0] }}–{{ difficultyRange[1] }}</span>
            </div>
            <InputNumber v-model="minYear" placeholder="Min Year" :use-grouping="false" class="w-28" />
            <InputNumber v-model="maxYear" placeholder="Max Year" :use-grouping="false" class="w-28" />
            <div class="flex items-center gap-2">
              <ToggleSwitch v-model="useRegex" />
              <span class="text-sm">Regex</span>
            </div>
            <InputNumber v-model="maxSnippets" :min="1" :max="50" prefix="Max: " class="w-28" />
          </div>
          <div class="flex gap-2">
            <Button label="Search" icon="pi pi-search" :loading="loading" @click="search" />
            <Button label="Export HTML" icon="pi pi-download" severity="secondary" :loading="exporting" :disabled="!searchResponse" @click="exportHtml" />
          </div>
        </div>
      </template>
    </Card>

    <div v-if="errorMsg" class="mb-4 rounded bg-red-900/50 p-3 text-red-200">{{ errorMsg }}</div>

    <div v-if="loading" class="flex justify-center py-12">
      <ProgressSpinner />
    </div>

    <template v-if="searchResponse && !loading">
      <Card v-if="searchResponse.results.length > 1" class="mb-4">
        <template #title>Comparison Overview</template>
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
            <Column header="Dialogue %">
              <template #body="{ data }">{{ data.dialogueWeightedAvg.toFixed(1) }}%</template>
            </Column>
          </DataTable>

          <div v-if="coOccurrences.length > 0" class="mt-4">
            <h3 class="mb-2 text-sm font-semibold text-surface-300">Co-occurrence (shared decks)</h3>
            <DataTable :value="coOccurrences" size="small" striped-rows>
              <Column field="termA" header="Term A" />
              <Column field="termB" header="Term B" />
              <Column field="sharedDecks" header="Shared Decks" />
            </DataTable>
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
            <h3 class="mb-2 font-semibold text-surface-200">Temporal Trends (Frequency by Release Year (per million chars))</h3>
            <Line :data="combinedTrendChartData" :options="trendLineOptions" />
          </template>
        </Card>
      </template>

      <template v-if="!isAllView && activeResult">
        <Card class="mb-4">
          <template #title>{{ activeResult.term }}</template>
          <template #subtitle>
            {{ activeResult.matchingDecks.toLocaleString() }} matching decks ·
            {{ activeResult.totalOccurrences.toLocaleString() }} occurrences ·
            {{ activeResult.hitsPerMillion.toFixed(1) }} occ/M chars · Avg dialogue: {{ activeResult.dialogueWeightedAvg.toFixed(1) }}%
          </template>
          <template #content>
            <div class="grid grid-cols-1 gap-6 lg:grid-cols-2">
              <div v-if="mediaChartData">
                <h3 class="mb-2 font-semibold text-surface-200">Media Type Breakdown (occ/M chars)</h3>
                <Bar :data="mediaChartData" :options="defaultBarOptions" />
              </div>

              <div v-if="difficultyChartData">
                <h3 class="mb-2 font-semibold text-surface-200">Difficulty Distribution</h3>
                <Bar :data="difficultyChartData" :options="difficultyBarOptions" />
              </div>
            </div>

            <div v-if="trendChartData" class="mt-6">
              <h3 class="mb-2 font-semibold text-surface-200">Temporal Trends (Frequency by Release Year (per million chars))</h3>
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
          <template #title>Concordance ({{ activeResult.snippets.length }} snippets)</template>
          <template #content>
            <DataTable :value="activeResult.snippets" size="small" striped-rows paginator :rows="20">
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
                  <NuxtLink :to="`/media/${data.deckId}`" class="text-primary-400 hover:underline">{{ data.deckTitle }}</NuxtLink>
                </template>
              </Column>
              <Column header="Type">
                <template #body="{ data }">{{ getMediaTypeText(data.mediaType) }}</template>
              </Column>
              <Column header="Diff" style="width: 4rem">
                <template #body="{ data }">{{ data.difficulty.toFixed(1) }}</template>
              </Column>
              <Column field="releaseYear" header="Year" style="width: 4rem" />
            </DataTable>
          </template>
        </Card>
      </template>
    </template>
  </div>
</template>

<style scoped>
  :deep(.snippet-text .keyword) {
    background: rgb(var(--p-primary-500));
    color: rgb(var(--p-surface-900));
    padding: 0 2px;
    border-radius: 2px;
    font-weight: 700;
  }
</style>
