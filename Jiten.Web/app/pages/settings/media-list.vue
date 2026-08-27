<script setup lang="ts">
  import type { VocabularyOption } from '~/components/VocabularyOptionGrid.vue';
  import { useAuthStore } from '~/stores/authStore';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Media List - Settings - Jiten' });

  const route = useRoute();
  const router = useRouter();
  const { $api } = useNuxtApp();
  const toast = useToast();
  const auth = useAuthStore();

  type Mode = 'import' | 'export';

  const mode = computed<Mode>({
    get: () => ((route.query.mode as string) === 'export' ? 'export' : 'import'),
    set: (v: Mode) => router.replace({ query: { mode: v } }),
  });

  const providerOptions: VocabularyOption[] = [
    { key: 'anilist', label: 'AniList', desc: 'Anime & manga lists from your public profile.', icon: 'pi pi-cloud-download' },
    { key: 'vndb', label: 'VNDB', desc: 'Visual novel list from your public profile.', icon: 'pi pi-cloud-download' },
    { key: 'file', label: 'Jiten export', desc: 'Restore a CSV or JSON file you exported from Jiten.', icon: 'pi pi-file-import' },
  ];

  const provider = computed<string | null>({
    get: () => {
      const raw = route.query.provider as string | undefined;
      return raw && providerOptions.some((o) => o.key === raw) ? raw : null;
    },
    set: (v: string | null) => router.replace({ query: { ...route.query, provider: v } }),
  });

  const modeOptions = [
    { label: 'Import', value: 'import' },
    { label: 'Export', value: 'export' },
  ];

  const exporting = ref(false);

  async function exportList(format: 'csv' | 'json') {
    exporting.value = true;
    try {
      const blob = await $api<Blob>(`user/media-list/export?format=${format}`, { responseType: 'blob' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `jiten-media-list.${format}`;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      toast.add({ severity: 'error', summary: 'Export failed', detail: 'Could not export your media list. Please try again.', life: 6000 });
    } finally {
      exporting.value = false;
    }
  }

  const mediaListLink = computed(() => (auth.user?.userName ? `/profile/${auth.user.userName}/media` : '/profile'));
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 flex flex-col gap-4">
    <div class="flex items-center gap-2">
      <NuxtLink to="/settings">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded />
      </NuxtLink>
      <h1 class="text-2xl font-bold">Media List</h1>
    </div>

    <p class="text-sm text-gray-600 dark:text-gray-400">
      Sync your
      <NuxtLink :to="mediaListLink">media list</NuxtLink>
      from AniList or VNDB with what is present on Jiten, restore a file you exported before, or export it.
    </p>

    <section>
      <div class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest mb-3">Mode</div>
      <SelectButton :model-value="mode" :options="modeOptions" option-value="value" option-label="label" @update:model-value="mode = $event" />
    </section>

    <template v-if="mode === 'import'">
      <section>
        <div class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest mb-3">Import from</div>
        <VocabularyOptionGrid v-model="provider" :options="providerOptions" />
      </section>

      <MediaListImportPanel v-if="provider === 'anilist'" provider="anilist" />
      <MediaListImportPanel v-if="provider === 'vndb'" provider="vndb" />
      <MediaListImportPanel v-if="provider === 'file'" provider="file" />
    </template>

    <Card v-else>
      <template #title>
        <h3 class="text-lg font-semibold">Export your media list</h3>
      </template>
      <template #content>
        <div class="flex flex-col gap-4">
          <p class="text-sm text-gray-600 dark:text-gray-400">
            The file contains every tracked title with its status, favourite flag, Jiten link and external links.
          </p>
          <div class="flex flex-wrap gap-2">
            <Button label="Download CSV" icon="pi pi-file-export" :disabled="exporting" @click="exportList('csv')" />
            <Button label="Download JSON" icon="pi pi-file-export" severity="secondary" :disabled="exporting" @click="exportList('json')" />
          </div>
        </div>
      </template>
    </Card>
  </div>
</template>
