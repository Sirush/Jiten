<script setup lang="ts">
  import { ref } from 'vue';
  import { useToast } from 'primevue/usetoast';
  import { extractApiError } from '~/utils/toast';
  import type { Deck } from '~/types';

  type ParseCustomDeckResponse = {
    deck?: Deck;
    file?: { contentBase64?: string; contentType?: string; fileName?: string };
  };

  const { $api } = useNuxtApp();
  const toast = useToast();

  const userText = ref('');
  const downloading = ref(false);
  const downloadInfo = ref<{ url: string; filename: string } | null>(null);
  const deckInfo = ref<Deck | null>(null);
  const errorMessage = ref<string | null>(null);

  const updateDeck = (updatedDeck: Deck) => {
    deckInfo.value = updatedDeck;
  };

  const parseDeck = async () => {
    if (downloadInfo.value) {
      window.URL.revokeObjectURL(downloadInfo.value.url);
      downloadInfo.value = null;
    }

    // Reset deck info
    deckInfo.value = null;
    errorMessage.value = null;

    try {
      downloading.value = true;
      const url = `media-deck/parse-custom-deck`;

      // Expect a standard JSON response now, so remove responseType: 'blob'
      const response = await $api<ParseCustomDeckResponse>(url, {
        method: 'POST',
        body: {
          text: userText.value,
        },
      });

      // Store the deck information
      if (response && response.deck) {
        deckInfo.value = response.deck;
        deckInfo.value!.originalTitle = 'Custom Deck';
      }

      if (response && response.file && response.file.contentBase64) {
        // Decode the Base64 string into a Blob
        const byteCharacters = atob(response.file.contentBase64);
        const byteNumbers = new Array(byteCharacters.length);
        for (let i = 0; i < byteCharacters.length; i++) {
          byteNumbers[i] = byteCharacters.charCodeAt(i);
        }
        const byteArray = new Uint8Array(byteNumbers);
        const blob = new Blob([byteArray], { type: response.file.contentType });

        // Create the download link from the decoded blob
        downloadInfo.value = {
          url: window.URL.createObjectURL(blob),
          filename: 'custom-deck.apkg',
        };
      } else {
        errorMessage.value = 'The deck was parsed but no Anki file came back. Please try again.';
        toast.add({ severity: 'error', summary: 'Deck file missing', detail: errorMessage.value, life: 6000 });
      }
    } catch (err) {
      const status = (err as { statusCode?: number; status?: number })?.statusCode ?? (err as { status?: number })?.status;
      errorMessage.value =
        status === 429
          ? 'Too many parse requests in a short time. Please wait a moment and try again.'
          : extractApiError(err, 'Your text could not be parsed. Please try again.');
      toast.add({ severity: 'error', summary: 'Parsing failed', detail: errorMessage.value, life: 6000 });
    } finally {
      downloading.value = false;
    }
  };

  const triggerDownload = () => {
    if (!downloadInfo.value) return; // Safety check

    const link = document.createElement('a');
    link.href = downloadInfo.value.url;
    link.setAttribute('download', downloadInfo.value.filename);
    document.body.appendChild(link);
    link.click();
    link.remove();
  };

  onUnmounted(() => {
    if (downloadInfo.value) {
      window.URL.revokeObjectURL(downloadInfo.value.url);
    }
  });
</script>

<template>
  <Card class="mx-auto shadow-lg">
    <template #title>
      <div class="flex items-center">
        <Icon name="material-symbols:text-snippet-outline" class="mr-2 text-primary" size="24" />
        <span class="text-xl">Parse Custom Deck</span>
      </div>
    </template>
    <template #content>
      <div class="flex flex-col gap-6">
        <div class="bg-blue-50 dark:bg-blue-950/40 p-4 rounded-lg border-l-4 border-blue-500 dark:border-blue-400">
          <h3 class="font-medium text-blue-800 dark:text-blue-200 mb-1">About this tool</h3>
          <p class="text-sm text-blue-700 dark:text-blue-300">
            You can parse up to 200,000 characters into a custom deck here. Your data will be processed temporarily on the server and be discarded afterwards.
            You will get stats about the text and an Anki deck, but it won't be permanently stored.
          </p>
        </div>

        <Message v-if="errorMessage" severity="error" :closable="false">{{ errorMessage }}</Message>

        <MediaDeckCard v-if="deckInfo != null" :deck="deckInfo" :hide-control="true" @update:deck="updateDeck" />

        <Button v-if="downloadInfo" severity="info" class="w-64 transition-colors self-center" :disabled="!downloadInfo" @click="triggerDownload">
          <Icon name="material-symbols:download" class="mr-2" />
          <span>Download Deck</span>
        </Button>

        <div class="bg-gray-50 dark:bg-gray-800/60 p-4 rounded-lg">
          <h3 class="font-medium mb-2 text-gray-700 dark:text-gray-200">Your text</h3>
          <Textarea
            v-model="userText"
            class="w-full border border-gray-300 dark:border-gray-600 rounded-md"
            rows="10"
            maxlength="200000"
            placeholder="Paste your Japanese text here..."
          />
          <div class="mt-2 text-right text-sm text-gray-600 dark:text-gray-400">
            Characters left: <b>{{ (200000 - userText.length).toLocaleString() }}</b>
          </div>
        </div>
        <div class="flex flex-col sm:flex-row gap-3 items-center justify-center">
          <Button class="w-64 bg-primary hover:bg-primary-dark transition-colors" :disabled="userText.length == 0" @click="parseDeck">
            <Icon name="material-symbols:upload" class="mr-2" />
            <span>Parse Text</span>
          </Button>
        </div>
      </div>

      <div v-if="downloading" class="fixed inset-0 flex items-center justify-center bg-black bg-opacity-70" style="z-index: 9999">
        <div class="bg-white dark:bg-gray-900 p-6 rounded-lg shadow-lg text-center max-w-md">
          <h3 class="text-xl font-bold text-primary mb-4">Processing Your Text</h3>
          <ProgressSpinner
            style="width: 60px; height: 60px"
            stroke-width="6"
            fill="transparent"
            animation-duration=".5s"
            aria-label="Creating your deck"
            class="mb-4"
          />
          <p class="text-gray-700 dark:text-gray-300">Analysing your text and creating your deck. This may take a few seconds...</p>
        </div>
      </div>
    </template>
  </Card>
</template>

<style scoped></style>
