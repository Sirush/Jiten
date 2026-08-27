<script setup lang="ts">
  import { ref, computed, onMounted } from 'vue';
  import { useDebounceFn } from '@vueuse/core';
  import DataTable from 'primevue/datatable';
  import Column from 'primevue/column';
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import InputText from 'primevue/inputtext';
  import Select from 'primevue/select';
  import Tag from 'primevue/tag';
  import ProgressSpinner from 'primevue/progressspinner';
  import { useToast } from 'primevue/usetoast';
  import type { MissingFuriganaItem, MissingFuriganaPaginatedResponse, WordFormsResponse, WordFormSummary } from '~/types';

  useHead({ title: 'Furigana Issues - Jiten Admin' });
  definePageMeta({ middleware: ['auth-admin'] });

  const { $api } = useNuxtApp();
  const toast = useToast();

  const items = ref<MissingFuriganaItem[]>([]);
  const totalCount = ref(0);
  const loading = ref(false);
  const searchQuery = ref('');
  const sortBy = ref('wordId');
  const sortOptions = [
    { label: 'Word ID', value: 'wordId' },
    { label: 'Global Frequency', value: 'frequency' },
  ];
  const currentPage = ref(0);
  const rowsPerPage = ref(50);

  const showEditDialog = ref(false);
  const editingWordId = ref<number | null>(null);
  const editingForms = ref<WordFormSummary[]>([]);
  const editingPartsOfSpeech = ref<string[]>([]);
  const editingRubyTexts = ref<Record<number, string>>({});
  const originalRubyTexts = ref<Record<number, string>>({});
  const saving = ref(false);

  async function loadItems() {
    loading.value = true;
    try {
      const params = new URLSearchParams();
      params.append('limit', rowsPerPage.value.toString());
      params.append('offset', (currentPage.value * rowsPerPage.value).toString());
      if (searchQuery.value.trim()) params.append('search', searchQuery.value.trim());
      if (sortBy.value !== 'wordId') params.append('sortBy', sortBy.value);

      const result = await $api<MissingFuriganaPaginatedResponse>(`/admin/words/missing-furigana?${params.toString()}`);
      items.value = result.items;
      totalCount.value = result.totalCount;
    } catch (error: any) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: error?.data?.message || 'Failed to load furigana issues',
        life: 5000,
      });
    } finally {
      loading.value = false;
    }
  }

  const debouncedSearch = useDebounceFn(() => {
    currentPage.value = 0;
    loadItems();
  }, 400);

  function onSortChange() {
    currentPage.value = 0;
    loadItems();
  }

  function onPage(event: any) {
    currentPage.value = event.page;
    rowsPerPage.value = event.rows;
    loadItems();
  }

  async function openEditDialog(item: MissingFuriganaItem) {
    try {
      const wordData = await $api<WordFormsResponse>(`/admin/words/${item.wordId}/forms`);
      editingWordId.value = item.wordId;
      editingForms.value = wordData.forms;
      editingPartsOfSpeech.value = wordData.partsOfSpeech;

      const rubyMap: Record<number, string> = {};
      const origMap: Record<number, string> = {};
      for (const form of wordData.forms) {
        rubyMap[form.readingIndex] = form.rubyText;
        origMap[form.readingIndex] = form.rubyText;
      }
      editingRubyTexts.value = rubyMap;
      originalRubyTexts.value = origMap;
      showEditDialog.value = true;
    } catch (error: any) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: error?.data?.message || 'Failed to load word forms',
        life: 5000,
      });
    }
  }

  const hasChanges = computed(() => {
    for (const form of editingForms.value) {
      if (editingRubyTexts.value[form.readingIndex] !== originalRubyTexts.value[form.readingIndex]) {
        return true;
      }
    }
    return false;
  });

  async function saveChanges() {
    if (!editingWordId.value || !hasChanges.value) return;

    saving.value = true;
    let savedCount = 0;
    let failedCount = 0;

    for (const form of editingForms.value) {
      const newRuby = editingRubyTexts.value[form.readingIndex];
      const oldRuby = originalRubyTexts.value[form.readingIndex];

      if (newRuby === oldRuby) continue;

      try {
        await $api(`/admin/words/${editingWordId.value}/forms/${form.readingIndex}/ruby-text`, {
          method: 'PUT',
          body: { rubyText: newRuby },
        });
        originalRubyTexts.value[form.readingIndex] = newRuby;
        savedCount++;
      } catch (error: any) {
        failedCount++;
        toast.add({
          severity: 'error',
          summary: 'Error',
          detail: `Failed to update form ${form.readingIndex}: ${error?.data?.message || error?.message || 'Unknown error'}`,
          life: 5000,
        });
      }
    }

    saving.value = false;

    if (savedCount > 0) {
      toast.add({
        severity: 'success',
        summary: 'Saved',
        detail: `Updated ${savedCount} form(s) for word ${editingWordId.value}`,
        life: 3000,
      });
    }

    if (failedCount === 0) {
      showEditDialog.value = false;

      const wordId = editingWordId.value!;
      const updatedRubyTexts = { ...editingRubyTexts.value };

      const updatedAllForms = editingForms.value.map((f) => ({
        ...f,
        rubyText: updatedRubyTexts[f.readingIndex] ?? f.rubyText,
      }));

      let removedCount = 0;
      items.value = items.value.filter((item) => {
        if (item.wordId !== wordId) return true;
        const newRuby = updatedRubyTexts[item.readingIndex];
        if (newRuby !== undefined && newRuby.includes('[')) {
          removedCount++;
          return false;
        }
        item.allForms = updatedAllForms;
        item.rubyText = updatedRubyTexts[item.readingIndex] ?? item.rubyText;
        return true;
      });

      totalCount.value = Math.max(0, totalCount.value - removedCount);
    }
  }

  function isKana(ch: string): boolean {
    const code = ch.charCodeAt(0);
    return (code >= 0x3040 && code <= 0x309f) || (code >= 0x30a0 && code <= 0x30ff);
  }

  function toHiragana(ch: string): string {
    const code = ch.charCodeAt(0);
    if (code >= 0x30a1 && code <= 0x30f6) return String.fromCharCode(code - 0x60);
    return ch;
  }

  function kanaEqual(a: string, b: string): boolean {
    return toHiragana(a) === toHiragana(b);
  }

  function guessRubyText(kanjiText: string, kanaText: string): string | null {
    const chars = [...kanjiText];
    const kana = [...kanaText];

    let prefixKana = 0;
    while (prefixKana < chars.length && prefixKana < kana.length && isKana(chars[prefixKana]) && kanaEqual(chars[prefixKana], kana[prefixKana])) prefixKana++;

    let suffixKana = 0;
    while (
      suffixKana < chars.length - prefixKana &&
      suffixKana < kana.length - prefixKana &&
      isKana(chars[chars.length - 1 - suffixKana]) &&
      kanaEqual(chars[chars.length - 1 - suffixKana], kana[kana.length - 1 - suffixKana])
    )
      suffixKana++;

    const midKanji = chars.slice(prefixKana, chars.length - suffixKana);
    const midKana = kana.slice(prefixKana, kana.length - suffixKana);

    if (midKanji.length === 0 || midKana.length === 0) return null;

    const kanjiRuns: { start: number; end: number }[] = [];
    let i = 0;
    while (i < midKanji.length) {
      if (!isKana(midKanji[i])) {
        const start = i;
        while (i < midKanji.length && !isKana(midKanji[i])) i++;
        kanjiRuns.push({ start, end: i });
      } else {
        i++;
      }
    }

    if (kanjiRuns.length === 0) return null;

    if (kanjiRuns.length === 1 && midKanji.every((ch) => !isKana(ch))) {
      const prefix = chars.slice(0, prefixKana).join('');
      const suffix = chars.slice(chars.length - suffixKana).join('');
      const kanjiPart = midKanji.join('');
      const reading = midKana.join('');
      return `${prefix}${kanjiPart}[${reading}]${suffix}`;
    }

    // Multiple kanji runs separated by kana — try to align the kana separators
    const kanaSegments: string[] = [];
    for (let j = 0; j < midKanji.length; j++) {
      if (isKana(midKanji[j])) {
        if (kanaSegments.length === 0 || !isKana(midKanji[j - 1])) kanaSegments.push('');
        kanaSegments[kanaSegments.length - 1] += midKanji[j];
      }
    }

    // Split midKana by the kana separators found in midKanji (normalize to hiragana for matching)
    const midKanaStr = midKana.map(toHiragana).join('');
    const parts: string[] = [];
    let remaining = midKanaStr;
    let valid = true;
    for (const sep of kanaSegments) {
      const normalizedSep = [...sep].map(toHiragana).join('');
      const idx = remaining.indexOf(normalizedSep);
      if (idx === -1) {
        valid = false;
        break;
      }
      parts.push(remaining.slice(0, idx));
      remaining = remaining.slice(idx + normalizedSep.length);
    }
    if (valid) parts.push(remaining);

    if (!valid || parts.length !== kanjiRuns.length) return null;
    if (parts.some((p) => p.length === 0)) return null;

    let result = chars.slice(0, prefixKana).join('');
    const midIdx = 0;
    let runIdx = 0;
    for (let j = 0; j < midKanji.length; j++) {
      if (!isKana(midKanji[j])) {
        if (j === kanjiRuns[runIdx].start) {
          const kanjiPart = midKanji.slice(kanjiRuns[runIdx].start, kanjiRuns[runIdx].end).join('');
          result += `${kanjiPart}[${parts[runIdx]}]`;
          j = kanjiRuns[runIdx].end - 1;
          runIdx++;
        }
      } else {
        result += midKanji[j];
      }
    }
    result += chars.slice(chars.length - suffixKana).join('');
    return result;
  }

  function findBestGuess(form: WordFormSummary): string | null {
    if (form.formType !== 0) return null;
    const kanaForms = editingForms.value.filter((f) => f.formType === 1);
    if (kanaForms.length === 0) return null;

    const sameIndex = kanaForms.find((f) => f.readingIndex === form.readingIndex);
    if (sameIndex) {
      const guess = guessRubyText(form.text, sameIndex.text);
      if (guess) return guess;
    }

    for (const kf of kanaForms) {
      if (kf === sameIndex) continue;
      const guess = guessRubyText(form.text, kf.text);
      if (guess) return guess;
    }
    return null;
  }

  function autoFillRubyText(form: WordFormSummary) {
    const guess = findBestGuess(form);
    if (guess) editingRubyTexts.value[form.readingIndex] = guess;
  }

  function canAutoFill(form: WordFormSummary): boolean {
    return findBestGuess(form) !== null;
  }

  function autoFillAll() {
    for (const form of editingForms.value) {
      if (form.formType === 0 && !editingRubyTexts.value[form.readingIndex]?.includes('[')) {
        autoFillRubyText(form);
      }
    }
  }

  const canAutoFillAny = computed(() =>
    editingForms.value.some((f) => f.formType === 0 && !editingRubyTexts.value[f.readingIndex]?.includes('[') && canAutoFill(f))
  );

  function getFormTypeLabel(formType: number) {
    return formType === 0 ? 'Kanji' : 'Kana';
  }

  function getFormTypeSeverity(formType: number): string {
    return formType === 0 ? 'info' : 'secondary';
  }

  onMounted(() => loadItems());
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Furigana Issues</h1>
    </div>

    <!-- Stats -->
    <div class="mb-4 flex items-center gap-4">
      <Tag severity="warn" class="text-base px-3 py-1">{{ totalCount }} kanji forms missing furigana</Tag>
      <InputText v-model="searchQuery" placeholder="Search by kanji text..." class="w-64" @input="debouncedSearch" />
      <Select v-model="sortBy" :options="sortOptions" option-label="label" option-value="value" class="w-48" @change="onSortChange" />
    </div>

    <!-- Loading -->
    <div v-if="loading && items.length === 0" class="flex flex-col gap-2 justify-center items-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" stroke-width="4" />
      <div>Loading furigana issues...</div>
    </div>

    <!-- DataTable -->
    <DataTable
      v-else
      :value="items"
      :loading="loading"
      :lazy="true"
      :paginator="true"
      :rows="rowsPerPage"
      :total-records="totalCount"
      :rows-per-page-options="[25, 50, 100]"
      :first="currentPage * rowsPerPage"
      striped-rows
      class="shadow-md"
      @page="onPage"
    >
      <Column field="wordId" header="Word ID" style="width: 100px">
        <template #body="{ data }">
          <NuxtLink :to="`/vocabulary/${data.wordId}/0`" target="_blank" class="text-blue-500 hover:underline">
            {{ data.wordId }}
          </NuxtLink>
        </template>
      </Column>
      <Column field="text" header="Kanji Form" style="width: 200px">
        <template #body="{ data }">
          <span class="text-lg font-bold">{{ data.text }}</span>
        </template>
      </Column>
      <Column header="All Forms" style="min-width: 250px">
        <template #body="{ data }">
          <div class="flex flex-wrap gap-1">
            <Tag v-for="form in data.allForms" :key="form.readingIndex" :severity="getFormTypeSeverity(form.formType)">
              {{ form.text }}
              <span v-if="form.rubyText" class="ml-1 opacity-70">({{ form.rubyText }})</span>
            </Tag>
          </div>
        </template>
      </Column>
      <Column field="frequencyRank" header="Frequency" style="width: 120px">
        <template #body="{ data }">
          <span v-if="data.frequencyRank" class="text-sm">{{ data.frequencyRank.toLocaleString() }}</span>
          <span v-else class="text-sm text-gray-400">—</span>
        </template>
      </Column>
      <Column field="partsOfSpeech" header="POS" style="width: 200px">
        <template #body="{ data }">
          <span class="text-sm text-gray-500 dark:text-gray-400">{{ data.partsOfSpeech.join(', ') }}</span>
        </template>
      </Column>
      <Column header="Actions" style="width: 100px">
        <template #body="{ data }">
          <Button icon="pi pi-pencil" size="small" severity="secondary" @click="openEditDialog(data)" />
        </template>
      </Column>
    </DataTable>

    <!-- Edit Dialog -->
    <Dialog v-model:visible="showEditDialog" :header="`Edit Furigana — Word ${editingWordId}`" :modal="true" class="w-full md:w-2/3 lg:w-1/2">
      <div class="mb-4 flex items-center justify-between">
        <span class="text-sm text-gray-500 dark:text-gray-400">POS: {{ editingPartsOfSpeech.join(', ') }}</span>
        <Button v-if="canAutoFillAny" label="Auto-fill All" icon="pi pi-sparkles" size="small" severity="help" @click="autoFillAll" />
      </div>

      <div class="flex flex-col gap-4">
        <div v-for="form in editingForms" :key="form.readingIndex" class="border rounded-lg p-3">
          <div class="flex items-center gap-3 mb-2">
            <Tag :severity="getFormTypeSeverity(form.formType)">
              {{ getFormTypeLabel(form.formType) }}
            </Tag>
            <span class="text-lg font-bold">{{ form.text }}</span>
            <span class="text-sm text-gray-400">index: {{ form.readingIndex }}</span>
          </div>
          <div>
            <label class="block text-sm font-medium mb-1">Ruby Text</label>
            <div class="flex gap-2">
              <InputText v-model="editingRubyTexts[form.readingIndex]" :placeholder="`e.g. ${form.text}`" class="w-full" />
              <Button
                v-if="canAutoFill(form)"
                v-tooltip.top="'Guess furigana from kana form'"
                icon="pi pi-sparkles"
                size="small"
                severity="help"
                @click="autoFillRubyText(form)"
              />
            </div>
            <div v-if="editingRubyTexts[form.readingIndex] !== originalRubyTexts[form.readingIndex]" class="text-xs mt-1 text-orange-500">
              Changed from: "{{ originalRubyTexts[form.readingIndex] || '(empty)' }}"
            </div>
          </div>
        </div>
      </div>

      <template #footer>
        <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showEditDialog = false" />
        <Button label="Save Changes" icon="pi pi-check" :loading="saving" :disabled="!hasChanges || saving" @click="saveChanges" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped></style>
