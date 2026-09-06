<script setup lang="ts">
  import { ref, computed, onMounted } from 'vue';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import InputNumber from 'primevue/inputnumber';
  import InputGroup from 'primevue/inputgroup';
  import DatePicker from 'primevue/datepicker';
  import Message from 'primevue/message';
  import SelectButton from 'primevue/selectbutton';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import CoverImageField from '~/components/dashboard/CoverImageField.vue';

  useHead({
    title: 'Add YouTube Source - Jiten',
  });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  interface YouTubePreview {
    kind: string;
    sourceId: string;
    title: string;
    channelName: string;
    channelId: string | null;
    description: string | null;
    coverUrl: string | null;
    coverDataUrl: string | null;
    url: string;
    conflict: string | null;
  }

  interface Registration {
    id: number;
    url: string;
    originalTitle: string | null;
    createdAt: string;
    completedAt: string | null;
    deckId: number | null;
    lastError: string | null;
    command: string;
  }

  type Mode = 'cli' | 'server';

  const toast = useToast();
  const route = useRoute();
  const { $api } = useNuxtApp();

  const modeOptions = [
    { label: 'Complete from my CLI', value: 'cli' },
    { label: 'Resolve on the server', value: 'server' },
  ];
  const mode = ref<Mode>('cli');
  const serverFetch = ref(false);
  const ingestConfigured = ref(true);
  const registrations = ref<Registration[]>([]);
  const lastCommand = ref<string | null>(null);

  const url = ref('');
  const preview = ref<YouTubePreview | null>(null);
  const fetching = ref(false);
  const submitting = ref(false);
  const previewError = ref<string | null>(null);

  const originalTitle = ref('');
  const romajiTitle = ref('');
  const englishTitle = ref('');
  const romanizing = ref(false);
  const releaseDate = ref<Date | null>(null);
  const coverImage = ref<File | null>(null);
  const coverImageUrl = ref<string | null>(null);

  const titleInclude = ref('');
  const titleExclude = ref('');
  const minMinutes = ref<number | null>(null);
  const maxMinutes = ref<number | null>(null);

  // Server mode needs a successful preview first; CLI mode only needs a URL
  const canSubmit = computed(() => (mode.value === 'cli' ? !!url.value.trim() : !!preview.value && !preview.value.conflict && !!originalTitle.value.trim()));
  const openRegistrations = computed(() => registrations.value.filter((r) => !r.completedAt));
  const completedRegistrations = computed(() => registrations.value.filter((r) => r.completedAt));

  const loadRegistrations = async () => {
    try {
      const data = await $api<{ serverFetch: boolean; ingestConfigured: boolean; registrations: Registration[] }>('admin/youtube-registrations');
      serverFetch.value = data.serverFetch;
      ingestConfigured.value = data.ingestConfigured;
      registrations.value = data.registrations;
    } catch {
      registrations.value = [];
    }
  };

  const fetchPreview = async () => {
    if (!url.value.trim()) return;
    fetching.value = true;
    preview.value = null;
    previewError.value = null;
    try {
      preview.value = await $api<YouTubePreview>('admin/youtube-preview', { query: { url: url.value.trim() } });
      originalTitle.value = preview.value.title;
      romajiTitle.value = '';
      englishTitle.value = '';
      releaseDate.value = null;
      coverImage.value = null;
      coverImageUrl.value = preview.value.coverDataUrl;
    } catch (error) {
      previewError.value = extractApiError(error, 'Could not read this source.');
    } finally {
      fetching.value = false;
    }
  };

  const autoRomanize = async () => {
    if (!originalTitle.value.trim()) return;
    romanizing.value = true;
    try {
      const data = await $api<{ romaji: string }>('utils/romanize', { method: 'POST', body: { title: originalTitle.value.trim() } });
      romajiTitle.value = data.romaji;
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to auto-romanize the title.'), life: 5000 });
    } finally {
      romanizing.value = false;
    }
  };

  const toIsoDate = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;

  const resetForm = () => {
    preview.value = null;
    url.value = '';
    originalTitle.value = '';
    romajiTitle.value = '';
    englishTitle.value = '';
    releaseDate.value = null;
    coverImage.value = null;
    coverImageUrl.value = null;
    titleInclude.value = '';
    titleExclude.value = '';
    minMinutes.value = null;
    maxMinutes.value = null;
  };

  const submit = async () => {
    if (!canSubmit.value) return;
    submitting.value = true;
    try {
      const formData = new FormData();
      formData.append('url', url.value.trim());
      formData.append('viaCli', String(mode.value === 'cli'));
      formData.append('originalTitle', originalTitle.value.trim());
      formData.append('romajiTitle', romajiTitle.value.trim());
      formData.append('englishTitle', englishTitle.value.trim());
      if (releaseDate.value) formData.append('releaseDate', toIsoDate(releaseDate.value));
      if (coverImage.value) formData.append('coverImage', coverImage.value);
      if (titleInclude.value.trim()) formData.append('titleInclude', titleInclude.value.trim());
      if (titleExclude.value.trim()) formData.append('titleExclude', titleExclude.value.trim());
      if (minMinutes.value) formData.append('minRuntimeSeconds', String(minMinutes.value * 60));
      if (maxMinutes.value) formData.append('maxRuntimeSeconds', String(maxMinutes.value * 60));

      const result = await $api<{ command?: string }>('admin/add-youtube-source', { method: 'POST', body: formData });

      if (mode.value === 'cli') {
        lastCommand.value = result.command ?? null;
        toast.add({ severity: 'success', summary: 'Saved', detail: 'Run the command below from your CLI to finish.', life: 5000 });
      } else {
        toast.add({ severity: 'success', summary: 'Registration queued', detail: 'The channel is being listed on the server.', life: 5000 });
      }
      resetForm();
      await loadRegistrations();
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to save the source.'), life: 5000 });
    } finally {
      submitting.value = false;
    }
  };

  const cancelRegistration = async (id: number) => {
    try {
      await $api(`admin/youtube-registrations/${id}`, { method: 'DELETE' });
      await loadRegistrations();
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Could not cancel.'), life: 4000 });
    }
  };

  const copy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      toast.add({ severity: 'success', summary: 'Copied', life: 2000 });
    } catch {
      toast.add({ severity: 'warn', summary: 'Copy failed', detail: 'Select the command and copy it by hand.', life: 4000 });
    }
  };

  const formatDateTime = (value: string) => new Date(value).toLocaleString();

  onMounted(async () => {
    await loadRegistrations();
    mode.value = serverFetch.value ? 'server' : 'cli';

    // Arrived from a media request's fulfil link
    const queryUrl = route.query.url;
    const initial = Array.isArray(queryUrl) ? queryUrl[0] : queryUrl;
    if (!initial) return;
    url.value = initial;
    if (mode.value === 'server') fetchPreview();
  });
</script>

<template>
  <div class="container mx-auto p-4">
    <h1 class="text-3xl font-bold mb-2">Add YouTube Source</h1>
    <p class="text-surface-500 dark:text-surface-400 mb-6">
      Paste a channel or playlist URL. Every video with manual Japanese subtitles becomes a subdeck, and new uploads are picked up automatically.
    </p>

    <div class="max-w-2xl mb-6">
      <SelectButton v-model="mode" :options="modeOptions" option-label="label" option-value="value" :allow-empty="false" class="mb-2" />
      <p class="text-xs text-surface-500 dark:text-surface-400 mb-4">
        <template v-if="mode === 'cli'">
          The server only stores what you enter here. Your CLI lists the channel with its own yt-dlp, sends the result back, and fetches the videos.
        </template>
        <template v-else>The server runs yt-dlp itself. Only works from an IP YouTube does not bot-check.</template>
      </p>

      <Message v-if="mode === 'cli' && !ingestConfigured" severity="warn" :closable="false" class="mb-4">
        The server has no YouTube ingest key configured, so the CLI will be refused. Set YOUTUBE_INGEST_KEY and redeploy.
      </Message>

      <label for="sourceUrl" class="block mb-2 font-medium">Channel or playlist URL</label>
      <InputGroup v-if="mode === 'server'">
        <InputText id="sourceUrl" v-model="url" placeholder="https://www.youtube.com/@handle" :disabled="fetching" @keydown.enter="fetchPreview" />
        <Button label="Check" icon="pi pi-search" :loading="fetching" :disabled="!url.trim()" @click="fetchPreview" />
      </InputGroup>
      <InputText v-else id="sourceUrl" v-model="url" class="w-full" placeholder="https://www.youtube.com/@handle" />
      <Message v-if="previewError" severity="error" :closable="false" class="mt-3">{{ previewError }}</Message>
    </div>

    <div v-if="mode === 'cli' || preview" class="grid grid-cols-1 lg:grid-cols-3 gap-6">
      <div class="lg:col-span-2">
        <div v-if="preview" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 p-4 mb-6">
          <h2 class="text-xl font-bold mb-1">
            <a :href="preview.url" target="_blank" rel="noopener" class="hover:underline">
              {{ preview.title }}
              <i class="pi pi-external-link text-sm align-middle text-surface-400" />
            </a>
          </h2>
          <div class="flex flex-wrap items-center gap-2 text-sm text-surface-500 dark:text-surface-400 mb-2">
            <Tag :value="preview.kind" severity="info" />
            <span>{{ preview.sourceId }}</span>
            <span v-if="preview.kind === 'Playlist'">· {{ preview.channelName }}</span>
          </div>
          <p v-if="preview.description" class="text-sm whitespace-pre-line line-clamp-4">{{ preview.description }}</p>
        </div>

        <Message v-if="preview?.conflict" severity="warn" :closable="false" class="mb-6">{{ preview.conflict }}</Message>

        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 p-4 mb-6">
          <h3 class="font-semibold mb-1">Deck</h3>
          <p v-if="mode === 'cli'" class="text-xs text-surface-500 dark:text-surface-400 mb-4">All optional. Empty fields take the channel's own name, avatar and oldest upload date.</p>
          <div v-else class="mb-4" />
          <div class="mb-4">
            <label for="originalTitle" class="block text-sm font-medium mb-1">Original Title</label>
            <InputText id="originalTitle" v-model="originalTitle" class="w-full" :placeholder="mode === 'cli' ? 'channel name' : ''" />
          </div>
          <div class="mb-4">
            <label for="romajiTitle" class="block text-sm font-medium mb-1">Romaji Title</label>
            <div class="flex gap-2">
              <InputText id="romajiTitle" v-model="romajiTitle" class="flex-1" />
              <Tooltip content="Auto-romanize from the original title">
                <Button :disabled="!originalTitle.trim() || romanizing" aria-label="Auto-romanize" @click="autoRomanize">
                  <Icon v-if="!romanizing" name="material-symbols-light:translate" size="1.5em" />
                  <Icon v-else name="line-md:loading-loop" size="1.5em" />
                </Button>
              </Tooltip>
            </div>
          </div>
          <div class="mb-4">
            <label for="englishTitle" class="block text-sm font-medium mb-1">English Title</label>
            <InputText id="englishTitle" v-model="englishTitle" class="w-full" />
          </div>
          <div>
            <label for="releaseDate" class="block text-sm font-medium mb-1">Release date</label>
            <DatePicker v-model="releaseDate" input-id="releaseDate" date-format="yy-mm-dd" show-icon class="w-full" placeholder="oldest video's upload date" />
            <p class="text-xs text-surface-500 dark:text-surface-400 mt-1">YouTube does not expose channel creation dates. Empty uses the oldest upload.</p>
          </div>
        </div>

        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 p-4 mb-6">
          <h3 class="font-semibold mb-4">Which videos</h3>
          <div class="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-4">
            <div>
              <label for="titleInclude" class="block text-sm font-medium mb-1">Only titles matching</label>
              <InputText id="titleInclude" v-model="titleInclude" class="w-full" placeholder="regex, optional" />
              <p class="text-xs text-surface-500 dark:text-surface-400 mt-1">Carves one series out of a mixed channel.</p>
            </div>
            <div>
              <label for="titleExclude" class="block text-sm font-medium mb-1">Skip titles matching</label>
              <InputText id="titleExclude" v-model="titleExclude" class="w-full" placeholder="regex, optional" />
              <p class="text-xs text-surface-500 dark:text-surface-400 mt-1">Shorts, announcements, member streams.</p>
            </div>
            <div class="md:col-span-2">
              <label for="minMinutes" class="block text-sm font-medium mb-1">Video length</label>
              <div class="flex items-center gap-2">
                <InputNumber v-model="minMinutes" input-id="minMinutes" :min="0" suffix=" min" placeholder="no minimum" fluid class="flex-1" />
                <span class="text-sm text-surface-500 dark:text-surface-400">to</span>
                <InputNumber v-model="maxMinutes" input-id="maxMinutes" :min="0" suffix=" min" placeholder="no maximum" fluid class="flex-1" />
              </div>
              <p class="text-xs text-surface-500 dark:text-surface-400 mt-1">For channels mixing shorts with long-form, or to keep multi-hour streams out.</p>
            </div>
          </div>
        </div>

        <Button
          :label="mode === 'cli' ? 'Save and get the command' : 'Register source'"
          :icon="mode === 'cli' ? 'pi pi-terminal' : 'pi pi-plus'"
          :loading="submitting"
          :disabled="!canSubmit"
          @click="submit"
        />

        <div v-if="lastCommand" class="mt-4 rounded-lg border border-primary-200 dark:border-primary-800 bg-primary-50 dark:bg-primary-950 p-4">
          <div class="text-sm font-medium mb-2">Run this from the repository root on your machine:</div>
          <div class="flex items-center gap-2">
            <code class="flex-1 text-sm px-3 py-2 rounded bg-surface-0 dark:bg-surface-900 overflow-x-auto">{{ lastCommand }}</code>
            <Button icon="pi pi-copy" size="small" severity="secondary" aria-label="Copy command" @click="copy(lastCommand!)" />
          </div>
          <p class="text-xs text-surface-500 dark:text-surface-400 mt-2">It lists the channel, sends the result here and fetches the videos. The deck appears once the server has parsed them.</p>
        </div>
      </div>

      <div>
        <CoverImageField v-model:file="coverImage" v-model:url="coverImageUrl" :title="originalTitle" :subtitle="romajiTitle || preview?.channelName || ''" />
        <p class="text-xs text-surface-500 dark:text-surface-400 mt-2">The channel avatar is used unless you upload or generate one.</p>
      </div>
    </div>

    <div v-if="registrations.length" class="mt-10 max-w-4xl">
      <h2 class="text-xl font-semibold mb-3">Waiting for the CLI</h2>
      <div v-if="openRegistrations.length === 0" class="text-sm text-surface-500 dark:text-surface-400 mb-6">Nothing waiting.</div>
      <div v-else class="rounded-lg border border-surface-200 dark:border-surface-700 overflow-hidden mb-6">
        <div
          v-for="registration in openRegistrations"
          :key="registration.id"
          class="flex flex-wrap items-center gap-x-4 gap-y-2 px-3 py-2 border-b border-surface-100 dark:border-surface-800 last:border-b-0 text-sm"
        >
          <div class="min-w-0 flex-1">
            <div class="font-medium truncate">{{ registration.originalTitle || registration.url }}</div>
            <div class="text-xs text-surface-500 dark:text-surface-400 truncate">{{ registration.url }} · {{ formatDateTime(registration.createdAt) }}</div>
            <div v-if="registration.lastError" class="text-xs text-red-600 dark:text-red-400 mt-1">{{ registration.lastError }}</div>
          </div>
          <code class="text-xs px-2 py-1 rounded bg-surface-100 dark:bg-surface-800">{{ registration.command }}</code>
          <Button icon="pi pi-copy" size="small" severity="secondary" text rounded aria-label="Copy command" @click="copy(registration.command)" />
          <Button icon="pi pi-times" size="small" severity="danger" text rounded aria-label="Cancel" @click="cancelRegistration(registration.id)" />
        </div>
      </div>

      <template v-if="completedRegistrations.length">
        <h3 class="font-semibold mb-2 text-surface-600 dark:text-surface-300">Completed this week</h3>
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 overflow-hidden">
          <div
            v-for="registration in completedRegistrations"
            :key="registration.id"
            class="flex flex-wrap items-center gap-x-4 gap-y-1 px-3 py-2 border-b border-surface-100 dark:border-surface-800 last:border-b-0 text-sm"
          >
            <NuxtLink :to="`/dashboard/media/${registration.deckId}`" class="font-medium hover:underline truncate">
              {{ registration.originalTitle || registration.url }}
            </NuxtLink>
            <span class="text-xs text-surface-500 dark:text-surface-400">deck {{ registration.deckId }} · {{ formatDateTime(registration.completedAt!) }}</span>
          </div>
        </div>
      </template>
    </div>
  </div>
</template>
