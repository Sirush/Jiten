<script setup lang="ts">
  import { useApiFetch } from '~/composables/useApiFetch';
  import Button from 'primevue/button';
  import Card from 'primevue/card';
  import DataTable from 'primevue/datatable';
  import Column from 'primevue/column';
  import { useToast } from 'primevue/usetoast';
  import { type GlobalStats, MediaType } from '~/types';
  import { getMediaTypePluralText, getMediaTypeSlug, getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { extractApiError } from '~/utils/toast';

  const { $api } = useNuxtApp();
  const toast = useToast();

  const description =
    'Free Japanese frequency dictionaries built from anime, drama, movies, novels, visual novels, manga and more. ' +
    'Yomitan-compatible .zip and CSV downloads, licensed CC BY-SA 4.0.';

  useSeoMeta({
    title: 'Japanese Frequency Dictionaries for Yomitan (Free Download)',
    description,
    ogTitle: 'Japanese Frequency Dictionaries for Yomitan',
    ogDescription: description,
    twitterTitle: 'Japanese Frequency Dictionaries for Yomitan',
    twitterDescription: description,
  });

  defineOgImage('PageOgImage', {
    title: 'Japanese Frequency Dictionaries',
    category: 'Yomitan & CSV downloads',
    description: 'Free frequency dictionaries for Yomitan, built per media type from the Jiten corpus. CC BY-SA 4.0.',
  });

  const downloadingKey = ref<string | null>(null);
  const downloadKey = (mediaType: MediaType | null | 'kanji', downloadType: 'yomitan' | 'csv') => `${mediaType ?? 'global'}-${downloadType}`;

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

  const typePhrase = (key: string, singular: string, plural: string) => {
    const count = response.value?.mediaByType?.[key];
    if (!count) return plural;
    return `${count.toLocaleString()} ${count === 1 ? singular : plural}`;
  };

  const listDescriptions = computed<{ name: string; text: string }[]>(() => [
    {
      name: 'Global',
      text: 'Every media type combined into one list. This should be the default frequency dictionary you install, helping you find which words are common in Japanese media as a whole.',
    },
    { name: 'Anime', text: `Built from the Japanese subtitles of ${typePhrase('Anime', 'anime', 'anime')}.` },
    { name: 'Audio', text: `Built from the transcripts of ${typePhrase('Audio', 'audio work', 'audio works')}.` },
    { name: 'Drama', text: `Built from the subtitles of ${typePhrase('Drama', 'live-action drama', 'live-action dramas')}.` },
    { name: 'Manga', text: `Built from the text of ${typePhrase('Manga', 'manga', 'manga')}.` },
    { name: 'Movie', text: `Built from the subtitles of ${typePhrase('Movie', 'film', 'films')}.` },
    { name: 'Non-Fiction', text: `Built from the text of ${typePhrase('NonFiction', 'non-fiction book', 'non-fiction books')}.` },
    { name: 'Novel', text: `Built from the text of ${typePhrase('Novel', 'published novel', 'published novels')}.` },
    { name: 'Video Game', text: `Built from the scripts of ${typePhrase('VideoGame', 'video game', 'video games')}.` },
    { name: 'Visual Novel', text: `Built from the scripts of ${typePhrase('VisualNovel', 'visual novel', 'visual novels')}.` },
    { name: 'Web Novel', text: `Built from ${typePhrase('WebNovel', 'serialized web novel', 'serialized web novels')}.` },
    {
      name: 'Kanji',
      text: 'Ranks individual kanji rather than words, by how often each character appears across the corpus.',
    },
  ]);

  const downloadFrequencyList = async (mediaType: MediaType | null | 'kanji', downloadType: 'yomitan' | 'csv') => {
    if (downloadingKey.value) return;
    downloadingKey.value = downloadKey(mediaType, downloadType);
    try {
      let url = '';
      let fileName = '';

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
        url = 'frequency-list/download?downloadType=yomitan';
        if (mediaType != null) url += `&mediaType=${mediaType}`;
        fileName = mediaType === null ? 'jiten_freq_global.zip' : `jiten_freq_${MediaType[mediaType]}.zip`;

        const response = await $api<Blob>(url, {
          method: 'GET',
          responseType: 'blob',
        });

        if (response) {
          const blob = new Blob([response], { type: 'application/zip' });

          const blobUrl = window.URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = blobUrl;
          link.setAttribute('download', fileName);
          document.body.appendChild(link);
          link.click();
          link.remove();

          window.URL.revokeObjectURL(blobUrl);
        }
      } else {
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
      plural: getMediaTypePluralText(value as MediaType),
      slug: getMediaTypeSlug(value as MediaType),
      id: value as MediaType,
    }))
    .sort((a, b) => a.name.localeCompare(b.name));
</script>

<template>
  <div class="py-2">
    <header class="mb-6">
      <h1 class="text-3xl font-bold mb-2">Japanese Frequency Dictionaries (Yomitan & CSV)</h1>
      <p class="text-sm text-surface-600 dark:text-surface-300">
        Word frequency lists built from the Japanese media analysed on Jiten
        <template v-if="status === 'success' && response">
          , currently {{ response.totalMojis?.toLocaleString() }} characters across {{ response.totalMedia?.toLocaleString() }} titles
        </template>
        . There is a global list, one list per media type, and a kanji list, each available as a Yomitan frequency dictionary or a plain CSV.
      </p>
    </header>

    <Card class="mb-4">
      <template #title>Downloads</template>
      <template #content>
        <div class="mb-3 text-sm text-surface-500 dark:text-surface-400">
          All frequency lists are licensed under
          <a href="https://creativecommons.org/licenses/by-sa/4.0/" target="_blank" rel="noopener noreferrer" class="underline">CC BY-SA 4.0</a>
          .
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
            Filter by media type, genre, tag, year or difficulty or hand-pick decks.
            <JitenPlusBadge />
          </span>
        </div>
      </template>
    </Card>

    <Card class="mb-4">
      <template #title>What's in each list</template>
      <template #content>
        <p class="mb-4 text-surface-600 dark:text-surface-300">
          The frequency of words can change dramatically depending on the corpus they come from. For example, a word that's common in novels can be rare in
          anime and the other way around. Pick the list that matches what you read or watch, or even multiple of them.
        </p>
        <div class="grid grid-cols-1 md:grid-cols-2 gap-x-8 gap-y-4">
          <section v-for="list in listDescriptions" :key="list.name">
            <h2 class="text-base font-semibold mb-1">{{ list.name }}</h2>
            <p class="text-sm text-surface-600 dark:text-surface-400">{{ list.text }}</p>
          </section>
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
        <div class="mt-4 pt-3 border-t border-surface-200 dark:border-surface-700 text-sm">
          <a
            href="https://greasyfork.org/en/scripts/549246-vndb-character-count"
            target="_blank"
            rel="noopener noreferrer"
            class="inline-flex items-center gap-1"
          >
            <Icon name="material-symbols-light:open-in-new" size="1.2em" />
            Userscript to display character count on VNDB
          </a>
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
          <b>{{ response.totalMojis?.toLocaleString() }}</b>
          characters in
          <b>{{ response.totalMedia?.toLocaleString() }}</b>
          media
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
        <p class="mb-3 text-sm text-surface-500 dark:text-surface-400">Every title ranked from easiest to hardest.</p>
        <div class="flex flex-wrap gap-2">
          <NuxtLink
            v-for="item in mediaTypesForDisplay"
            :key="item.id"
            :to="`/decks/media/list/${item.slug}`"
            class="px-3 py-2 border border-surface-200 dark:border-surface-700 rounded-md !no-underline !text-inherit hover:border-primary-400 hover:!text-primary-500"
          >
            {{ item.plural }}
          </NuxtLink>
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
