<script setup lang="ts">
  import { ref, computed } from 'vue';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import InputNumber from 'primevue/inputnumber';
  import InputGroup from 'primevue/inputgroup';
  import Message from 'primevue/message';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import CoverImageField from '~/components/dashboard/CoverImageField.vue';

  useHead({
    title: 'Add Webnovel - Jiten',
  });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  interface WebNovelPreview {
    provider: string;
    sourceId: string;
    url: string;
    title: string;
    titleConflict: boolean;
    author: string | null;
    synopsis: string | null;
    genre: string | null;
    keywords: string[];
    episodeCount: number;
    totalCharacters: number;
    isCompleted: boolean;
    isOnHiatus: boolean;
    isOneShot: boolean;
    isR15: boolean;
    firstPublishedAt: string | null;
    lastUpdatedAt: string | null;
    estimatedSubdecks: number;
  }

  const toast = useToast();
  const { $api } = useNuxtApp();

  const url = ref('');
  const preview = ref<WebNovelPreview | null>(null);
  const fetching = ref(false);
  const submitting = ref(false);

  const coverImage = ref<File | null>(null);
  const coverImageUrl = ref<string | null>(null);
  const chunkCharBudget = ref<number | null>(null);

  // A long novel is fetched one episode at a time at ~0.7s each, plus a pause every 10
  const estimatedMinutes = computed(() => {
    if (!preview.value) return 0;
    return Math.max(1, Math.round((preview.value.episodeCount * 0.7 + (preview.value.episodeCount / 10) * 5) / 60));
  });

  const fetchPreview = async () => {
    if (!url.value.trim()) return;

    fetching.value = true;
    preview.value = null;
    try {
      preview.value = await $api<WebNovelPreview>('admin/webnovel-preview', { query: { url: url.value.trim() } });
    } catch (error) {
      console.error(error);
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Could not read this novel.'), life: 5000 });
    } finally {
      fetching.value = false;
    }
  };

  const submit = async () => {
    if (!preview.value) return;

    const formData = new FormData();
    formData.append('url', preview.value.url);
    if (coverImage.value) formData.append('coverImage', coverImage.value);
    if (chunkCharBudget.value) formData.append('chunkCharBudget', String(chunkCharBudget.value));

    submitting.value = true;
    try {
      await $api('admin/add-webnovel-deck', { method: 'POST', body: formData });
      toast.add({
        severity: 'success',
        summary: 'Import queued',
        detail: `'${preview.value.title}' is being fetched — roughly ${estimatedMinutes.value} min.`,
        life: 5000,
      });

      url.value = '';
      preview.value = null;
      coverImage.value = null;
      coverImageUrl.value = null;
      chunkCharBudget.value = null;
    } catch (error) {
      console.error(error);
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to queue the import.'), life: 5000 });
    } finally {
      submitting.value = false;
    }
  };

  const formatNumber = (value: number) => value.toLocaleString('en-US');
  const formatDate = (value: string | null) => (value ? new Date(value).toLocaleDateString() : '—');
</script>

<template>
  <div class="container mx-auto p-4">
    <h1 class="text-3xl font-bold mb-2">Add Webnovel</h1>
    <p class="text-surface-500 dark:text-surface-400 mb-6">
      Paste a syosetu URL or ncode. The novel is split into chapter-range subdecks and new chapters are picked up automatically.
    </p>

    <div class="max-w-2xl mb-6">
      <label for="novelUrl" class="block mb-2 font-medium">Novel URL</label>
      <InputGroup>
        <InputText
          id="novelUrl"
          v-model="url"
          placeholder="https://ncode.syosetu.com/n9669bk/"
          :disabled="fetching"
          @keydown.enter="fetchPreview"
        />
        <Button label="Fetch" icon="pi pi-search" :loading="fetching" :disabled="!url.trim()" @click="fetchPreview" />
      </InputGroup>
    </div>

    <div v-if="preview" class="grid grid-cols-1 lg:grid-cols-3 gap-6">
      <div class="lg:col-span-2">
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 p-4">
          <h2 class="text-xl font-bold mb-1">
            <a :href="preview.url" target="_blank" rel="noopener" class="hover:underline">
              {{ preview.title }}
              <i class="pi pi-external-link text-sm align-middle text-surface-400" />
            </a>
          </h2>
          <div class="text-sm text-surface-500 dark:text-surface-400 mb-3">
            {{ preview.author }} · {{ preview.sourceId }}
          </div>

          <div class="flex flex-wrap gap-2 mb-4">
            <Tag v-if="preview.isCompleted" value="Completed" severity="success" />
            <Tag v-else value="Ongoing" severity="info" />
            <Tag v-if="preview.isOnHiatus" value="On hiatus" severity="warn" />
            <Tag v-if="preview.isOneShot" value="One-shot" severity="secondary" />
            <Tag v-if="preview.isR15" value="R15" severity="danger" />
            <Tag v-if="preview.genre" :value="preview.genre" severity="secondary" />
          </div>

          <p v-if="preview.synopsis" class="text-sm whitespace-pre-line mb-4 max-h-40 overflow-y-auto">{{ preview.synopsis }}</p>

          <div class="grid grid-cols-2 sm:grid-cols-4 gap-4 text-sm">
            <div>
              <div class="text-surface-500 dark:text-surface-400">Episodes</div>
              <div class="font-medium tabular-nums">{{ formatNumber(preview.episodeCount) }}</div>
            </div>
            <div>
              <div class="text-surface-500 dark:text-surface-400">Characters</div>
              <div class="font-medium tabular-nums">{{ formatNumber(preview.totalCharacters) }}</div>
            </div>
            <div>
              <div class="text-surface-500 dark:text-surface-400">First published</div>
              <div class="font-medium">{{ formatDate(preview.firstPublishedAt) }}</div>
            </div>
            <div>
              <div class="text-surface-500 dark:text-surface-400">Last updated</div>
              <div class="font-medium">{{ formatDate(preview.lastUpdatedAt) }}</div>
            </div>
          </div>

          <div v-if="preview.keywords.length" class="mt-4 flex flex-wrap gap-1">
            <span
              v-for="keyword in preview.keywords"
              :key="keyword"
              class="text-xs px-1.5 py-0.5 rounded bg-surface-100 dark:bg-surface-800 text-surface-600 dark:text-surface-300"
            >
              {{ keyword }}
            </span>
          </div>
        </div>

        <Message v-if="preview.titleConflict" severity="error" :closable="false" class="mt-4">
          A webnovel deck titled “{{ preview.title }}” already exists. Rename or remove it first — importing would otherwise be skipped.
        </Message>

        <Message severity="info" :closable="false" class="mt-4">
          Roughly {{ preview.estimatedSubdecks }} subdeck{{ preview.estimatedSubdecks === 1 ? '' : 's' }}. Fetching takes about
          {{ estimatedMinutes }} minute{{ estimatedMinutes === 1 ? '' : 's' }} at a polite request rate, so the import runs in the background.
        </Message>
      </div>

      <div>
        <CoverImageField v-model:file="coverImage" v-model:url="coverImageUrl" :title="preview.title" :subtitle="preview.author ?? ''" />
        <p class="mt-2 text-xs text-surface-500 dark:text-surface-400">
          Syosetu works have no cover art — upload one or generate it from the title.
        </p>

        <div class="mt-4">
          <label for="chunkChars" class="block mb-1 text-sm font-medium">Subdeck size (characters)</label>
          <InputNumber
            id="chunkChars"
            v-model="chunkCharBudget"
            placeholder="150000"
            :min="10000"
            :max="1000000"
            :step="10000"
            class="w-full"
            show-buttons
          />
          <p class="mt-1 text-xs text-surface-500 dark:text-surface-400">Leave empty for the default (150,000 — roughly one volume).</p>
        </div>

        <Button
          label="Import novel"
          icon="pi pi-download"
          class="w-full mt-6"
          severity="success"
          :loading="submitting"
          :disabled="preview.titleConflict"
          @click="submit"
        />
      </div>
    </div>
  </div>
</template>
