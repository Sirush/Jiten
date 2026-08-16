<script setup lang="ts">
  import { useApiFetch } from '~/composables/useApiFetch';
  import Button from 'primevue/button';
  import Card from 'primevue/card';
  import DataTable from 'primevue/datatable';
  import Column from 'primevue/column';
  import { useToast } from 'primevue/usetoast';
  import { type GlobalStats, MediaType, type Word } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { extractApiError } from '~/utils/toast';

  const { $api } = useNuxtApp();
  const toast = useToast();

  const downloadingKey = ref<string | null>(null);
  const downloadKey = (mediaType: MediaType | null | 'kanji', downloadType: 'yomitan' | 'csv') =>
    `${mediaType ?? 'global'}-${downloadType}`;

  // Create an array of all media types plus Global (sorted) and Kanji at the end
  const deckTypes = [
    { id: null, name: 'Global' },
    ...Object.values(MediaType)
      .filter((value) => typeof value === 'number')
      .map((value) => ({
        id: value as MediaType,
        name: getMediaTypeText(value as MediaType),
      }))
      .sort((a, b) => a.name.localeCompare(b.name)),
    { id: 'kanji' as const, name: 'Kanji' },
  ];

  const downloadFrequencyList = async (mediaType: MediaType | null | 'kanji', downloadType: 'yomitan' | 'csv') => {
    if (downloadingKey.value) return;
    downloadingKey.value = downloadKey(mediaType, downloadType);
    try {
      let url = '';
      let fileName = '';

      // Handle Kanji frequency list separately
      if (mediaType === 'kanji') {
        url = `frequency-list/download-kanji?downloadType=${downloadType}`;
        fileName = downloadType === 'yomitan' ? 'jiten_kanji_freq.zip' : 'jiten_kanji_freq.csv';

        const response = await $api<Blob>(url, {
          method: 'GET',
          responseType: 'blob',
        });

        if (response) {
          const mimeType = downloadType === 'yomitan' ? 'application/zip' : 'text/csv';
          const blob = new Blob([response], { type: mimeType });
          const blobUrl = window.URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = blobUrl;
          link.setAttribute('download', fileName);
          document.body.appendChild(link);
          link.click();
          link.remove();
          window.URL.revokeObjectURL(blobUrl);
        }
        return;
      }

      if (downloadType === 'yomitan') {
        // For Yomitan format, use the download endpoint
        url = 'frequency-list/download?downloadType=yomitan';
        if (mediaType != null) url += `&mediaType=${mediaType}`;
        fileName = mediaType === null ? 'jiten_freq_global.zip' : `jiten_freq_${MediaType[mediaType]}.zip`;

        const response = await $api<Blob>(url, {
          method: 'GET',
          responseType: 'blob',
        });

        if (response) {
          // Get the response as a blob for binary data
          const blob = new Blob([response], { type: 'application/zip' });

          // Create download link
          const blobUrl = window.URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = blobUrl;
          link.setAttribute('download', fileName);
          document.body.appendChild(link);
          link.click();
          link.remove();

          // Clean up the blob URL
          window.URL.revokeObjectURL(blobUrl);
        }
      } else {
        // For CSV format, use the existing logic
        url = 'frequency-list/download?downloadType=csv';
        if (mediaType != null) url += `&mediaType=${mediaType}`;
        fileName = mediaType === null ? 'frequency_list_global.csv' : `frequency_list_${MediaType[mediaType]}.csv`;

        const response = await $api(url);
        if (response) {
          const data = typeof response === 'string' ? response : JSON.stringify(response);
          const blob = new Blob([data], { type: 'text/csv' });
          const blobUrl = window.URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = blobUrl;
          link.setAttribute('download', fileName);
          document.body.appendChild(link);
          link.click();
          link.remove();
          window.URL.revokeObjectURL(blobUrl);
        } else {
          toast.add({
            severity: 'error',
            summary: 'Download failed',
            detail: 'The frequency list came back empty. Please try again.',
            life: 6000,
          });
        }
      }
    } catch (err) {
      const status = (err as { statusCode?: number; status?: number })?.statusCode ?? (err as { status?: number })?.status;
      const detail =
        status === 404
          ? 'This frequency list has not been generated yet. Please check back later.'
          : status === 429
            ? 'Too many downloads in a short time. Please wait a moment and try again.'
            : extractApiError(err, 'The download could not be completed. Please try again.');
      toast.add({ severity: 'error', summary: 'Download failed', detail, life: 6000 });
    } finally {
      downloadingKey.value = null;
    }
  };

  const globalStatsUrl = 'stats/get-global-stats';
  const { data: response, status, error } = await useApiFetch<GlobalStats>(globalStatsUrl);

  const mediaTypesForDisplay = Object.values(MediaType)
    .filter((value) => typeof value === 'number')
    .map((value) => ({
      name: getMediaTypeText(value as MediaType),
      id: value as MediaType,
    }))
    .sort((a, b) => a.name.localeCompare(b.name));
</script>

<template>
  <div>
    <Card class="mb-4">
      <template #title>Frequency Lists</template>
      <template #content>
        <div class="mb-3">Download frequency lists as a frequency dictionary for use with Yomitan or as a CSV.</div>
        <div class="mb-3 text-sm text-surface-500 dark:text-surface-400">
          All frequency lists are licensed under
          <a href="https://creativecommons.org/licenses/by-sa/4.0/" target="_blank" rel="noopener noreferrer" class="underline">CC BY-SA 4.0</a>.
        </div>
        <DataTable :value="deckTypes" class="p-datatable-sm frequency-table" striped-rows responsive-layout="scroll" show-gridlines row-hover>
          <Column field="name" header="Type" class="font-medium" header-style="background-color: var(--surface-100); font-weight: 600;" />
          <Column
            header="Yomitan Frequency Dictionary"
            style="width: 300px"
            header-style="background-color: var(--surface-100); font-weight: 600;"
            header-class="text-center"
            body-class="text-center"
          >
            <template #body="slotProps">
              <!-- Text weight, not filled: twelve rows of two downloads would otherwise read as
                   twenty-four primary actions and leave the page without an entry point. -->
              <Button
                text
                size="small"
                class="w-full"
                :loading="downloadingKey === downloadKey(slotProps.data.id, 'yomitan')"
                :disabled="downloadingKey !== null"
                @click="downloadFrequencyList(slotProps.data.id, 'yomitan')"
              >
                <Icon v-if="downloadingKey !== downloadKey(slotProps.data.id, 'yomitan')" name="material-symbols-light:download" class="mr-2" size="1.5em" />
                Yomitan
              </Button>
            </template>
          </Column>
          <Column
            header="Download CSV"
            style="width: 300px"
            header-style="background-color: var(--surface-100); font-weight: 600;"
            header-class="text-center"
            body-class="text-center"
          >
            <template #body="slotProps">
              <Button
                text
                size="small"
                class="w-full"
                :loading="downloadingKey === downloadKey(slotProps.data.id, 'csv')"
                :disabled="downloadingKey !== null"
                @click="downloadFrequencyList(slotProps.data.id, 'csv')"
              >
                <Icon v-if="downloadingKey !== downloadKey(slotProps.data.id, 'csv')" name="material-symbols-light:download" class="mr-2" size="1.5em" />
                CSV
              </Button>
            </template>
          </Column>
        </DataTable>
        <div class="mt-4 flex flex-col sm:flex-row sm:items-center gap-2">
          <JitenPlusGate feature="freq-list-generate" feature-label="Custom frequency lists" compact>
            <Button as="router-link" to="/jiten-plus/frequency-lists" severity="primary" outlined>
              <Icon name="material-symbols-light:tune" class="mr-2" size="1.4em" />
              Build your own custom list
            </Button>
          </JitenPlusGate>
          <span class="text-sm text-surface-500 dark:text-surface-400">
            Filter by media type, genre, tag, year or difficulty or hand-pick decks. <JitenPlusBadge />
          </span>
        </div>
      </template>
    </Card>

    <Card class="mb-4">
      <template #title>Tools</template>
      <template #content>
        <div class="flex flex-col sm:flex-row gap-3">
          <Button as="router-link" to="/media-updates" severity="secondary">
            <Icon name="material-symbols-light:breaking-news-alt-1-outline" class="mr-2" />
            View Media Updates
          </Button>
          <Button as="router-link" to="/parse-deck" severity="secondary">
            <Icon name="material-symbols-light:cards-star-outline" class="mr-2" />
            Create Custom Deck
          </Button>
        </div>
        <div>
          <ul>
            <li>
              <a href="https://greasyfork.org/en/scripts/549246-vndb-character-count" target="_blank">Userscript to display character count on VNDB</a>
            </li>
          </ul>
        </div>
      </template>
    </Card>

    <Card v-if="status === 'success'" class="mt-4">
      <template #title>
        <div class="flex items-center">
          <Icon name="material-symbols-light:bar-chart" class="mr-2 text-primary" size="1.5em" />
          Global Stats
        </div>
      </template>
      <template #content>
        <div class="mb-3">
          <b>{{ response.totalMojis?.toLocaleString() }}</b> characters in <b>{{ response.totalMedia?.toLocaleString() }}</b> media
        </div>

        <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          <div v-for="[mediaType, amount] in Object.entries(response.mediaByType)" :key="mediaType" class="p-2 border rounded-md">
            <div class="font-medium">
              {{ getMediaTypeText(MediaType[mediaType]) }}
            </div>
            <div class="text-lg font-bold text-primary-600">
              {{ amount?.toLocaleString() }}
            </div>
          </div>
        </div>
      </template>
    </Card>

    <Card class="mt-4">
      <template #title>
        <div class="flex items-center">
          <Icon name="material-symbols-light:manage-search" class="mr-2 text-primary" size="1.5em" />
          Media indexes by type
        </div>
      </template>
      <template #content>
        <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          <div v-for="item in mediaTypesForDisplay" :key="item.id" class="p-2 border rounded-md">
          <NuxtLink :to="`/decks/media/list/${item.id}`" target="_blank">{{ item.name }} index</NuxtLink>
          </div>
        </div>
      </template>
    </Card>
  </div>
</template>

<style scoped>
  .frequency-table {
    border-radius: var(--radius-lg);
    overflow: hidden;
    box-shadow: 0 2px 4px rgba(0, 0, 0, 0.05);
  }

  .frequency-table :deep(.p-datatable-header) {
    background-color: var(--surface-50);
    border-bottom: 1px solid var(--surface-200);
  }

  .frequency-table :deep(.p-datatable-thead) tr th {
    padding: 0.75rem 1rem;
    transition: background-color 0.2s;
  }

  /* PrimeVue sets text-align on the cell at a higher specificity than the text-center utility,
     so the Column header-class/body-class would otherwise have no effect. */
  .frequency-table :deep(.p-datatable-thead) tr th.text-center,
  .frequency-table :deep(.p-datatable-tbody) tr td.text-center {
    text-align: center;
  }

  .frequency-table :deep(.p-datatable-tbody) tr td {
    padding: 0.75rem 1rem;
    border-bottom: 1px solid var(--surface-200);
  }

  .frequency-table :deep(.p-datatable-tbody) tr:last-child td {
    border-bottom: none;
  }

  .frequency-table :deep(.p-datatable-tbody) tr.p-highlight {
    background-color: var(--primary-50);
  }
</style>
