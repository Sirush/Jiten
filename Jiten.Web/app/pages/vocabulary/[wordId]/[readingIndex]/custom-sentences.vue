<script setup lang="ts">
  import type { UserExampleSentenceDto, Word } from '~/types/types';
  import { stripRuby } from '~/utils/stripRuby';
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import Button from 'primevue/button';
  import Textarea from 'primevue/textarea';
  import InputText from 'primevue/inputtext';

  definePageMeta({ middleware: ['auth'] });

  const route = useRoute();
  const { $api } = useNuxtApp();
  const confirm = useConfirm();
  const toast = useToast();
  const { limits: planLimits, isPlus } = useJitenPlus();
  const sentenceLimit = computed(() => planLimits.value.customSentencesPerWord);
  const limitReachedSummary = computed(() => `Maximum of ${sentenceLimit.value} custom sentences reached`);
  const plusUpsell = computed(() => (isPlus.value ? '' : `Jiten+ raises this to ${planLimits.value.plus.customSentencesPerWord}.`));
  const wordId = Number(route.params.wordId) || 0;
  const readingIndex = Number(route.params.readingIndex) || 0;

  const { data: wordData } = await useApiFetch<Word>(`vocabulary/${wordId}/${readingIndex}/info`);

  const title = computed(() => {
    if (wordData.value?.mainReading?.text) return stripRuby(wordData.value.mainReading.text);
    return 'Word';
  });

  useHead(() => ({
    title: `Custom Sentences - ${title.value}`,
  }));

  const sentences = ref<UserExampleSentenceDto[]>([]);
  const editTexts = ref<Record<number, string>>({});
  const editSources = ref<Record<number, string>>({});
  const saving = ref<Record<number, boolean>>({});
  const editingId = ref<number | null>(null);
  const newText = ref('');
  const newSource = ref('');
  const adding = ref(false);
  const error = ref('');

  function hasValidMarkers(text: string): boolean {
    return /\*\*[^*]+\*\*/.test(text);
  }

  function markerHint(text: string): string | null {
    if (!text || hasValidMarkers(text)) return null;
    return 'Mark words to highlight with **, e.g. **食べる**';
  }

  async function loadSentences() {
    sentences.value = await $api<UserExampleSentenceDto[]>(`user/example-sentences/${wordId}/${readingIndex}`);
    editTexts.value = {};
    editSources.value = {};
    for (const s of sentences.value) {
      editTexts.value[s.userExampleSentenceId] = s.text;
      editSources.value[s.userExampleSentenceId] = s.source ?? '';
    }
  }

  await loadSentences();

  function previewHtml(text: string): string {
    if (!hasValidMarkers(text)) return sanitiseHtml(text);
    return parseCustomSentenceHtml(text);
  }

  async function addSentence() {
    error.value = '';
    if (!hasValidMarkers(newText.value)) return;
    if (newText.value.length > 150) return;
    adding.value = true;
    try {
      await $api(`user/example-sentences/${wordId}/${readingIndex}`, {
        method: 'POST',
        body: { text: newText.value, source: newSource.value || undefined },
      });
      newText.value = '';
      newSource.value = '';
      await loadSentences();
    } catch {
      toast.add({ severity: 'error', summary: limitReachedSummary.value, detail: plusUpsell.value || undefined, life: 3000 });
    } finally {
      adding.value = false;
    }
  }

  async function saveSentence(id: number) {
    error.value = '';
    const text = editTexts.value[id];
    if (!hasValidMarkers(text)) return;
    if (text.length > 150) return;
    saving.value = { ...saving.value, [id]: true };
    try {
      await $api(`user/example-sentences/${id}`, {
        method: 'PUT',
        body: { text, source: editSources.value[id] || undefined },
      });
      editingId.value = null;
      await loadSentences();
    } catch {
      error.value = 'Failed to save.';
    } finally {
      saving.value = { ...saving.value, [id]: false };
    }
  }

  function confirmDelete(id: number) {
    confirm.require({
      message: 'Are you sure you want to delete this custom sentence?',
      header: 'Delete Sentence',
      acceptLabel: 'Delete',
      rejectLabel: 'Cancel',
      accept: () => deleteSentence(id),
    });
  }

  async function deleteSentence(id: number) {
    try {
      await $api(`user/example-sentences/${id}`, { method: 'DELETE' });
      editingId.value = null;
      await loadSentences();
    } catch {
      error.value = 'Failed to delete.';
    }
  }

  function startEditing(id: number) {
    editingId.value = id;
  }

  function cancelEditing(s: UserExampleSentenceDto) {
    editTexts.value[s.userExampleSentenceId] = s.text;
    editSources.value[s.userExampleSentenceId] = s.source ?? '';
    editingId.value = null;
  }

  function isDirty(s: UserExampleSentenceDto): boolean {
    return editTexts.value[s.userExampleSentenceId] !== s.text || (editSources.value[s.userExampleSentenceId] ?? '') !== (s.source ?? '');
  }
</script>

<template>
  <Card class="p-4">
    <template #content>
      <div class="mb-4">
        <NuxtLink :to="`/vocabulary/${wordId}/${readingIndex}`" class="text-sm text-primary hover:underline">
          <i class="pi pi-arrow-left text-xs mr-1" />
          {{ title }}
        </NuxtLink>
      </div>

      <h1 class="text-2xl font-bold mb-2">Custom Example Sentences</h1>
      <p class="text-sm text-surface-400 mb-6">
        Up to {{ sentenceLimit }} custom sentences for <span class="font-bold">{{ title }}</span
        >. Surround words you want highlighted with <code class="bg-surface-100 dark:bg-surface-800 px-1 rounded">**</code>, e.g.
        <code class="bg-surface-100 dark:bg-surface-800 px-1 rounded">**{{ title }}**</code>
      </p>

      <div v-if="error" class="mb-4 p-3 rounded bg-red-50 dark:bg-red-900/20 text-red-600 dark:text-red-400 text-sm">
        {{ error }}
      </div>

      <div v-for="sentence in sentences" :key="sentence.userExampleSentenceId" class="mb-4">
        <template v-if="editingId === sentence.userExampleSentenceId">
          <div class="rounded-xl border border-primary-300 dark:border-primary-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
            <div class="mb-2">
              <label class="text-xs text-surface-400 block mb-1">Sentence</label>
              <Textarea
                v-model="editTexts[sentence.userExampleSentenceId]"
                rows="2"
                class="w-full"
                :maxlength="150"
                placeholder="彼は毎日**走る**ことにしている"
              />
              <div class="flex justify-between">
                <div v-if="markerHint(editTexts[sentence.userExampleSentenceId] ?? '')" class="text-xs text-orange-500">
                  {{ markerHint(editTexts[sentence.userExampleSentenceId] ?? '') }}
                </div>
                <div v-else />
                <div class="text-xs text-surface-400">{{ editTexts[sentence.userExampleSentenceId]?.length ?? 0 }}/150</div>
              </div>
            </div>
            <div class="mb-2">
              <label class="text-xs text-surface-400 block mb-1">Source</label>
              <InputText v-model="editSources[sentence.userExampleSentenceId]" class="w-full" :maxlength="150" placeholder="Naruto - Episode 1" />
            </div>
            <div v-if="hasValidMarkers(editTexts[sentence.userExampleSentenceId] ?? '')" class="mb-3">
              <label class="text-xs text-surface-400 block mb-1">Preview</label>
              <blockquote class="border-l-4 border-yellow-500 pl-4 py-2 bg-gray-50 dark:bg-gray-900 rounded-r text-sm">
                <div v-html="previewHtml(editTexts[sentence.userExampleSentenceId] ?? '')" />
              </blockquote>
            </div>
            <div class="flex gap-2 justify-end">
              <Button severity="danger" text size="small" icon="pi pi-trash" label="Delete" @click="confirmDelete(sentence.userExampleSentenceId)" />
              <Button text size="small" label="Cancel" @click="cancelEditing(sentence)" />
              <Button
                size="small"
                icon="pi pi-check"
                label="Save"
                :loading="saving[sentence.userExampleSentenceId]"
                :disabled="!isDirty(sentence) || !hasValidMarkers(editTexts[sentence.userExampleSentenceId] ?? '')"
                @click="saveSentence(sentence.userExampleSentenceId)"
              />
            </div>
          </div>
        </template>
        <template v-else>
          <div class="flex items-start gap-2 group cursor-pointer" @click="startEditing(sentence.userExampleSentenceId)">
            <CustomExampleSentenceEntry :sentence="sentence" class="flex-1" />
            <button class="text-surface-400 hover:text-primary-500 transition-colors mt-3 shrink-0 opacity-0 group-hover:opacity-100" title="Edit">
              <i class="pi pi-pencil text-sm" />
            </button>
          </div>
        </template>
      </div>

      <div v-if="sentences.length < sentenceLimit" class="rounded-xl border border-dashed border-surface-300 dark:border-surface-600 p-4">
        <h2 class="text-sm font-semibold mb-3">Add a new sentence</h2>
        <div class="mb-2">
          <Textarea v-model="newText" rows="2" class="w-full" :maxlength="150" placeholder="Put the target word between **stars**" />
          <div class="flex justify-between">
            <div v-if="markerHint(newText)" class="text-xs text-orange-500">{{ markerHint(newText) }}</div>
            <div v-else />
            <div class="text-xs text-surface-400">{{ newText.length }}/150</div>
          </div>
        </div>
        <div class="mb-2">
          <InputText v-model="newSource" class="w-full" :maxlength="150" placeholder="Source that will be displayed below the sentence (optional)" />
        </div>
        <div v-if="newText" class="mb-3">
          <label class="text-xs text-surface-400 block mb-1">Preview</label>
          <blockquote class="border-l-4 border-yellow-500 pl-4 py-2 bg-gray-50 dark:bg-gray-900 rounded-r text-sm">
            <div v-html="previewHtml(newText)" />
          </blockquote>
        </div>
        <div class="flex justify-end">
          <Button size="small" icon="pi pi-plus" label="Add" :loading="adding" :disabled="!hasValidMarkers(newText)" @click="addSentence" />
        </div>
      </div>

      <div v-else class="text-sm text-surface-400 italic">
        {{ limitReachedSummary }} for this word.
        <NuxtLink v-if="plusUpsell" to="/jiten-plus" class="text-primary not-italic hover:underline">{{ plusUpsell }}</NuxtLink>
      </div>
    </template>
  </Card>
</template>
