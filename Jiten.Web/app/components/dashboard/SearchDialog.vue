<script setup lang="ts">
import { ref, watch } from 'vue';
import Button from 'primevue/button';
import Dialog from 'primevue/dialog';
import Toast from 'primevue/toast';
import { useToast } from 'primevue/usetoast';
import ProgressSpinner from 'primevue/progressspinner';
import type { Metadata } from '~/types';

const props = defineProps<{
  visible: boolean;
  query: string;
  author?: string;
  provider: string;
}>();

const searchResults = ref<Metadata[]>([]);
const isLoading = ref(false);
const toast = useToast();
const { $api } = useNuxtApp();

const emit = defineEmits<{
  'update:visible': [value: boolean];
  'select-metadata': [metadata: Metadata];
  'link-metadata': [metadata: Metadata];
}>();

const searchAPI = async () => {
  isLoading.value = true;

  try {
    const url = 'admin/search-media';
    const response = await $api<Metadata[]>(url, {
      query: {
        provider: props.provider,
        query: props.query,
        author: props.author || '',
      },
    });

    console.log(response);
    searchResults.value = response || [];

    if (!response || response.length === 0) {
      console.log('No results found');
    }
  } catch (error) {
    console.error('Error searching API:', error);
    showToast('error', 'Search Error', 'An error occurred while searching. Please try again.');
    searchResults.value = [];
  } finally {
    isLoading.value = false;
  }
};

watch(() => props.visible, async (newValue) => {
  if (newValue && props.query) {
    await searchAPI();
  }
});

function showToast(severity: 'success' | 'info' | 'warn' | 'error', summary: string, detail: string = '') {
  toast.add({ severity, summary, detail, life: 3000 });
}

function selectMetadata(metadata: Metadata) {
  emit('select-metadata', metadata);
  emit('update:visible', false);
}

function linkMetadata(metadata: Metadata) {
  emit('link-metadata', metadata);
  emit('update:visible', false);
}

function closeDialog() {
  emit('update:visible', false);
}
</script>

<template>
  <Dialog
    :visible="visible"
    header="Search Results"
    :style="{ width: '90vw', maxWidth: '1200px' }"
    :modal="true"
    :closable="true"
    :close-on-escape="true"
    @update:visible="emit('update:visible', $event)"
  >
    <div class="p-4">
      <div v-if="isLoading" class="flex justify-center items-center py-12">
        <ProgressSpinner style="width: 50px; height: 50px" stroke-width="4" />
        <span class="ml-3">Searching...</span>
      </div>

      <!-- Results grid -->
      <div v-else-if="searchResults.length > 0" class="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4 max-h-[70vh] overflow-y-auto">
        <div
          v-for="(metadata, index) in searchResults"
          :key="index"
          class="border rounded-lg overflow-hidden shadow-sm hover:shadow-md transition-shadow"
        >
          <div class="h-48 bg-gray-200 flex items-center justify-center overflow-hidden">
            <img
              :src="metadata.image"
              :alt="metadata.englishTitle || metadata.romajiTitle || metadata.originalTitle"
              class="w-full h-full object-cover"
            >
          </div>
          <div class="p-3">
            <div class="text-sm text-gray-500 dark:text-gray-400 mb-2">
              {{ metadata.releaseDate ? new Date(metadata.releaseDate).toLocaleDateString() : 'Unknown date' }}
            </div>
            <div class="mb-3">
              <div v-if="metadata.originalTitle" class="font-medium">{{ metadata.originalTitle }}</div>
              <div v-if="metadata.romajiTitle" class="text-sm">{{ metadata.romajiTitle }}</div>
              <div v-if="metadata.englishTitle" class="text-sm italic">{{ metadata.englishTitle }}</div>
            </div>
            <div v-if="metadata.genres && metadata.genres.length > 0" class="text-xs text-gray-600 dark:text-gray-400 mb-2">
              <span
                v-for="genre in metadata.genres.slice(0, 3)"
                :key="genre"
                class="inline-block bg-purple-100 text-purple-800 px-2 py-1 rounded mr-1 mb-1"
              >
                {{ genre }}
              </span>
              <span v-if="metadata.genres.length > 3" class="text-gray-500 dark:text-gray-400">+{{ metadata.genres.length - 3 }}</span>
            </div>
            <div v-if="metadata.isAdultOnly" class="text-xs mb-2">
              <span class="inline-block bg-red-100 text-red-800 px-2 py-1 rounded">18+</span>
            </div>
            <div class="flex flex-col gap-1">
              <Button label="Select" class="w-full p-button-sm" @click="selectMetadata(metadata)" />
              <Button label="Link Only" severity="secondary" class="w-full p-button-sm" @click="linkMetadata(metadata)" />
            </div>
          </div>
        </div>
      </div>

      <!-- No results message -->
      <div v-else class="text-center py-8">
        <p class="text-gray-500 dark:text-gray-400">No results found. Try a different search term.</p>
      </div>
    </div>
  </Dialog>
</template>
