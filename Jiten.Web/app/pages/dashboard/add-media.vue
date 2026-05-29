<script setup lang="ts">
  import { computed, onBeforeUnmount, ref, watch } from 'vue'; // Added watch and onBeforeUnmount
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import FileUpload from 'primevue/fileupload';
  import InputText from 'primevue/inputtext';
  import Toast from 'primevue/toast';
  import { useToast } from 'primevue/usetoast';
  import type { Metadata, DuplicateCheckDeckDto } from '~/types';
  import { MediaType } from '~/types';
  import { getChildrenCountText, getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { getLinkTypeText } from '~/utils/linkTypeMapper';
  import SearchDialog from '~/components/dashboard/SearchDialog.vue';

  useHead({
    title: 'Add Media - Admin Dashboard - Jiten',
  });

  definePageMeta({
    middleware: ['auth'],
  });

  const SCREEN_MEDIA_TYPE = 'media-type';
  const SCREEN_FILE_UPLOAD = 'file-upload';
  const currentScreen = ref(SCREEN_MEDIA_TYPE);

  const selectedMediaType = ref<MediaType | null>(null);
  const toast = useToast();
  const { $api } = useNuxtApp();

  function showToast(severity: 'success' | 'info' | 'warn' | 'error', summary: string, detail: string = '') {
    toast.add({ severity, summary, detail, life: 3000 });
  }

  const selectedFile = ref<File | null>(null);
  const originalTitle = ref('');
  const duplicateDecks = ref<DuplicateCheckDeckDto[]>([]);

  let duplicateTimeout: ReturnType<typeof setTimeout> | null = null;
  watch(originalTitle, (newTitle) => {
    if (duplicateTimeout) clearTimeout(duplicateTimeout);
    if (newTitle.trim().length < 2) {
      duplicateDecks.value = [];
      return;
    }
    duplicateTimeout = setTimeout(async () => {
      duplicateDecks.value = await $api<DuplicateCheckDeckDto[]>('admin/duplicate-check', {
        query: { title: newTitle.trim(), mediaType: selectedMediaType.value },
      }) ?? [];
    }, 500);
  });

  const romajiTitle = ref('');
  const englishTitle = ref('');
  const romanizing = ref(false);
  const releaseDate = ref<Date>();
  const description = ref('');
  const rating = ref(0);

  const coverImage = ref<File | null>(null); // User uploaded file
  const coverImageUrl = ref<string | null>(null); // URL from API metadata
  const coverImageObjectUrl = ref<string | null>(null); // Local object URL for preview

  const searchQuery = ref('');
  const authorQuery = ref('');

  const searchResults = ref<Metadata[]>([]);
  const showSearchResultsDialog = ref(false);
  const selectedMetadata = ref<Metadata | null>(null);

  const newSubdeckUploaderRef = ref<InstanceType<typeof FileUpload> | null>(null);

  const subdecks = ref<
    Array<{
      id: number;
      originalTitle: string;
      file: File | null;
    }>
  >([]);
  let nextSubdeckId = 1;

  const mediaTypes = computed(() => {
    return Object.values(MediaType)
      .filter((value) => typeof value === 'number')
      .map((value) => value as MediaType)
      .sort((a, b) => getMediaTypeText(a).localeCompare(getMediaTypeText(b)));
  });

  const subdeckDefaultName = computed(() => {
    if (!selectedMediaType.value) return '';

    const baseText = getChildrenCountText(selectedMediaType.value);
    return baseText.endsWith('s') ? baseText.slice(0, -1) : baseText;
  });

  watch(coverImage, (newFile) => {
    if (coverImageObjectUrl.value) {
      URL.revokeObjectURL(coverImageObjectUrl.value);
      coverImageObjectUrl.value = null;
    }
    if (newFile && typeof window !== 'undefined') {
      coverImageObjectUrl.value = URL.createObjectURL(newFile);
    }
  });

  onBeforeUnmount(() => {
    if (coverImageObjectUrl.value) {
      URL.revokeObjectURL(coverImageObjectUrl.value);
      coverImageObjectUrl.value = null;
    }
    if (duplicateTimeout) clearTimeout(duplicateTimeout);
  });

  function selectMediaType(mediaType: MediaType) {
    selectedMediaType.value = mediaType;
    currentScreen.value = SCREEN_FILE_UPLOAD;
  }

  function handleFileUpload(event: { files: File[] }) {
    if (event.files && event.files.length > 0) {
      const file = event.files[0];
      selectedFile.value = file;
      const fileName = file.name;
      const lastDotIndex = fileName.lastIndexOf('.');
      searchQuery.value = lastDotIndex !== -1 ? fileName.substring(0, lastDotIndex) : fileName;
    }
  }

  function handleCoverImageUpload(event: { files: File[] }) {
    if (event.files && event.files.length > 0) {
      const file = event.files[0];
      coverImage.value = file;
      coverImageUrl.value = null;
    }
  }

  function clearCoverImage() {
    coverImageUrl.value = null;
    coverImage.value = null;
  }

  function handleSubdeckFileUpload(event: { files: File[] }, subdeckId: number) {
    if (event.files && event.files.length > 0) {
      const file = event.files[0];
      const subdeck = subdecks.value.find((sd) => sd.id === subdeckId);
      if (subdeck) {
        subdeck.file = file;
      }
    }
  }

  function handleNewSubdeckFileUpload(event: { files: File[] }) {
    if (event.files && event.files.length > 0) {
      let mainFileConvertedInThisBatch = false;

      for (const file of event.files) {
        // If this is the first subdeck, convert the main file to the first subdeck
        if (subdecks.value.length === 0 && selectedFile.value && !mainFileConvertedInThisBatch) {
          // Add the main file as the first subdeck
          subdecks.value.push({
            id: nextSubdeckId++,
            originalTitle: `${subdeckDefaultName.value} 1`,
            file: selectedFile.value,
          });
          mainFileConvertedInThisBatch = true;

          // Add the new file as the second subdeck
          subdecks.value.push({
            id: nextSubdeckId++,
            originalTitle: `${subdeckDefaultName.value} 2`,
            file: file,
          });
        } else {
          // If not the first subdeck, just add the new subdeck as before
          const newSubdeckNumber = subdecks.value.length + 1;
          subdecks.value.push({
            id: nextSubdeckId++,
            originalTitle: `${subdeckDefaultName.value} ${newSubdeckNumber}`,
            file: file,
          });
        }
      }
      
      // Explicitly clear the FileUpload component's selection
      if (newSubdeckUploaderRef.value) {
        newSubdeckUploaderRef.value.clear();
      }
    }
  }

  function removeSubdeck(id: number) {
    // Only allow removing the last subdeck and not the first one
    if (subdecks.value.length <= 1) {
      showToast('warn', 'Cannot Delete', 'You cannot delete the first subdeck');
      return;
    }

    const lastSubdeck = subdecks.value[subdecks.value.length - 1];
    if (lastSubdeck.id !== id) {
      showToast('warn', 'Cannot Delete', 'You can only delete the last subdeck');
      return;
    }

    // Remove the last subdeck
    subdecks.value.pop();
  }

  function searchAPI(apiName: string) {
    console.log(`Opening search dialog for ${apiName} with query: ${searchQuery.value}, Author: ${authorQuery.value}`);
    currentProvider.value = apiName;
    showSearchResultsDialog.value = true;
  }

  function handleSelectMetadata(metadata: Metadata) {
    selectedMetadata.value = metadata;
    originalTitle.value = metadata.originalTitle;
    romajiTitle.value = metadata.romajiTitle || '';
    englishTitle.value = metadata.englishTitle || '';
    description.value = metadata.description || '';
    releaseDate.value = new Date(metadata.releaseDate) || new Date();
    rating.value = metadata.rating || 0;

    if (metadata.image) {
      coverImageUrl.value = metadata.image;
      coverImage.value = null;
    } else {
      coverImageUrl.value = null;
    }
  }

  function handleLinkMetadata(metadata: Metadata) {
    if (!selectedMetadata.value) {
      handleSelectMetadata(metadata);
      return;
    }

    if (metadata.links) {
      for (const link of metadata.links) {
        if (!selectedMetadata.value.links.some(l => l.linkType === link.linkType))
          selectedMetadata.value.links.push(link);
      }
    }
  }

  const currentProvider = ref('');

  async function autoRomanize() {
    if (!originalTitle.value) return;
    romanizing.value = true;
    try {
      const data = await $api<{ romaji: string }>('utils/romanize', {
        method: 'POST',
        body: { title: originalTitle.value },
      });
      romajiTitle.value = data.romaji;
    } catch {
      showToast('error', 'Error', 'Failed to auto-romanize title');
    } finally {
      romanizing.value = false;
    }
  }

  function goBack() {
    if (currentScreen.value === SCREEN_FILE_UPLOAD) {
      currentScreen.value = SCREEN_MEDIA_TYPE;
      selectedMediaType.value = null;
      selectedFile.value = null;
      originalTitle.value = '';
      romajiTitle.value = '';
      englishTitle.value = '';
      releaseDate.value = new Date();
      description.value = '';
      clearCoverImage();
      searchQuery.value = '';
      authorQuery.value = '';
      searchResults.value = [];
      selectedMetadata.value = null;
      subdecks.value = [];
      rating.value = 0;
      duplicateDecks.value = [];
    }
  }

  async function submitMedia() {
    if (!originalTitle.value.trim()) {
      showToast('warn', 'Validation Error', 'Original title is required');
      return;
    }

    if (!coverImage.value && !coverImageUrl.value) {
      showToast('warn', 'Validation Error', 'Cover image is required');
      return;
    }

    try {
      // Prepare data
      const formData = new FormData();
      formData.append('mediaType', selectedMediaType.value?.toString() || '');
      formData.append('originalTitle', originalTitle.value);
      formData.append('romajiTitle', romajiTitle.value);
      formData.append('englishTitle', englishTitle.value);
      formData.append('releaseDate', formatDateAsYyyyMmDd(releaseDate.value));
      formData.append('description', description.value);
      formData.append('rating', rating.value);

      // Handle cover image
      if (coverImage.value) {
        formData.append('coverImage', coverImage.value);
      } else if (coverImageUrl.value) {
        formData.append('coverImage', coverImageUrl.value);
      }

      // Add links from metadata if available
      if (selectedMetadata.value && selectedMetadata.value.links && selectedMetadata.value.links.length > 0) {
        for (let i = 0; i < selectedMetadata.value.links.length; i++) {
          const link = selectedMetadata.value.links[i];
          formData.append(`links[${i}].url`, link.url);
          formData.append(`links[${i}].linkType`, link.linkType);
        }
      }

      // Add aliases from metadata if available
      if (selectedMetadata.value && selectedMetadata.value.aliases && selectedMetadata.value.aliases.length > 0) {
        for (let i = 0; i < selectedMetadata.value.aliases.length; i++) {
          const alias = selectedMetadata.value.aliases[i];
          formData.append(`aliases[${i}]`, alias);
        }
      }

      // Add genres from metadata if available
      if (selectedMetadata.value?.genres && selectedMetadata.value.genres.length > 0) {
        for (let i = 0; i < selectedMetadata.value.genres.length; i++) {
          formData.append(`genres[${i}]`, selectedMetadata.value.genres[i]);
        }
      }

      // Add tags from metadata if available
      if (selectedMetadata.value?.tags && selectedMetadata.value.tags.length > 0) {
        for (let i = 0; i < selectedMetadata.value.tags.length; i++) {
          const tag = selectedMetadata.value.tags[i];
          formData.append(`tags[${i}].name`, tag.name);
          formData.append(`tags[${i}].percentage`, String(tag.percentage ?? 0));
        }
      }

      // Add isAdultOnly from metadata if available
      if (selectedMetadata.value?.isAdultOnly !== undefined) {
        formData.append('isAdultOnly', selectedMetadata.value.isAdultOnly.toString());
      }

      // Add isNotOriginallyJapanese from metadata if available
      if (selectedMetadata.value?.isNotOriginallyJapanese !== undefined) {
        formData.append('isNotOriginallyJapanese', selectedMetadata.value.isNotOriginallyJapanese.toString());
      }

      // Add relations from metadata if available
      if (selectedMetadata.value?.relations && selectedMetadata.value.relations.length > 0) {
        for (let i = 0; i < selectedMetadata.value.relations.length; i++) {
          const relation = selectedMetadata.value.relations[i];
          formData.append(`relations[${i}].externalId`, relation.externalId);
          formData.append(`relations[${i}].linkType`, String(relation.linkType));
          formData.append(`relations[${i}].relationshipType`, String(relation.relationshipType));
          if (relation.targetMediaType !== undefined) {
            formData.append(`relations[${i}].targetMediaType`, String(relation.targetMediaType));
          }
          formData.append(`relations[${i}].swapDirection`, String(relation.swapDirection));
        }
      }

      if (selectedMetadata.value?.dictionaryEntries && selectedMetadata.value.dictionaryEntries.length > 0) {
        for (let i = 0; i < selectedMetadata.value.dictionaryEntries.length; i++) {
          formData.append(`dictionaryEntries[${i}]`, selectedMetadata.value.dictionaryEntries[i].surface);
        }
      }

      if (subdecks.value.length === 0 && selectedFile.value) {
        // If no subdecks, include the main file
        formData.append('file', selectedFile.value);
      } else if (subdecks.value.length > 0) {
        // Convert subdecks to JSON and append to formData
        for (let i = 0; i < subdecks.value.length; i++) {
          const subdeck = subdecks.value[i];
          if (subdeck.file) {
            formData.append(`subdecks[${i}].originalTitle`, subdeck.originalTitle);
            formData.append(`subdecks[${i}].file`, subdeck.file);
          }
        }
      }

      const data = await $api('admin/add-deck', {
        method: 'POST',
        body: formData,
      });

      showToast('success', 'Success', 'Media added successfully!');
      navigateTo('/dashboard');
    } catch (error) {
      console.error('Error submitting media:', error);
      showToast('error', 'Submission Error', 'An error occurred while submitting. Please try again.');
    }
  }
</script>

<template>
  <div>
    <div class="container mx-auto p-4">
      <div class="flex items-center mb-6">
        <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
        <h1 class="text-3xl font-bold">Add Media</h1>
      </div>

      <!-- Media Type Selection Screen -->
      <div v-if="currentScreen === SCREEN_MEDIA_TYPE" class="mt-6">
        <h2 class="text-xl font-semibold mb-4">Select Media Type</h2>
        <div class="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
          <Button
            v-for="mediaType in mediaTypes"
            :key="mediaType"
            :label="getMediaTypeText(mediaType)"
            class="p-button-outlined p-button-lg h-24"
            @click="selectMediaType(mediaType)"
          />
        </div>
      </div>

      <!-- File Upload Screen -->
      <div v-else-if="currentScreen === SCREEN_FILE_UPLOAD" class="mt-6">
        <div class="flex items-center mb-4">
          <Button class="p-button-text mr-2" @click="goBack">
            <Icon name="material-symbols-light:arrow-back" class="w-full" size="1.5em" />
          </Button>
          <h2 class="text-xl font-semibold">Upload {{ getMediaTypeText(selectedMediaType!) }}</h2>
        </div>

        <!-- Main file upload -->
        <Card v-if="!selectedFile" class="mb-6 p-4">
          <template #content>
            <FileUpload
              mode="advanced"
              :auto="true"
              choose-label="Select Main File"
              :multiple="false"
              class="w-full main-file-upload"
              :custom-upload="true"
              :show-upload-button="false"
              :show-cancel-button="false"
              @select="handleFileUpload"
            >
              <template #empty>
                <div class="flex items-center justify-center flex-col">
                  <Icon name="material-symbols-light:arrow-upload-progress" class="!border-2 !rounded-full !p-8 !text-4xl !text-muted-color" />
                  <p class="mt-6 mb-0">Drag and drop file to here to upload.</p>
                </div>
              </template>
            </FileUpload>
          </template>
        </Card>

        <!-- File details card -->
        <Card v-else class="mb-6">
          <template #title>Media Details</template>
          <template #content>
            <div class="grid grid-cols-1 md:grid-cols-2 gap-6">
              <div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Original Title</label>
                  <InputText v-model="originalTitle" class="w-full" />
                  <div v-if="duplicateDecks.length > 0" class="mt-2">
                    <p class="text-sm font-semibold text-orange-600 dark:text-orange-400 mb-1">This media may already exist:</p>
                    <div v-for="deck in duplicateDecks" :key="deck.deckId" class="flex items-center gap-2 text-sm py-1">
                      <NuxtLink :to="`/decks/media/${deck.deckId}/detail`" class="text-primary hover:underline" target="_blank">
                        {{ deck.title }}
                      </NuxtLink>
                    </div>
                  </div>
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Romaji Title</label>
                  <div class="flex gap-2">
                    <InputText v-model="romajiTitle" class="flex-1" />
                    <Button
                      v-tooltip.top="'Auto-romanize from original title'"
                      :disabled="!originalTitle || romanizing"
                      @click="autoRomanize"
                    >
                      <Icon v-if="!romanizing" name="material-symbols-light:translate" size="1.5em" />
                      <Icon v-else name="line-md:loading-loop" size="1.5em" />
                    </Button>
                  </div>
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">English Title</label>
                  <InputText v-model="englishTitle" class="w-full" />
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Release Date</label>
                  <DatePicker v-model="releaseDate" class="w-full" />
                </div>
                <div v-if="selectedMetadata?.links?.length" class="mb-4">
                  <label class="block text-sm font-medium mb-1">Links</label>
                  <div class="flex flex-wrap gap-2">
                    <a
                      v-for="link in selectedMetadata.links"
                      :key="link.url"
                      :href="link.url"
                      target="_blank"
                      rel="noopener noreferrer"
                      class="text-sm text-primary underline"
                    >{{ getLinkTypeText(link.linkType) }}</a>
                  </div>
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Description</label>
                  <Textarea v-model="description" class="w-full" :rows="6" />
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Selected File</label>
                  <div class="flex items-center">
                    <span class="text-sm text-gray-600">{{ selectedFile.name }}</span>
                    <Button class="p-button-text p-button-sm ml-2" @click="selectedFile = null">
                      <Icon name="material-symbols-light:close" class="w-full" size="1.5em" />
                    </Button>
                  </div>
                </div>
              </div>

              <div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Cover Image</label>
                  <div v-if="coverImageUrl || coverImageObjectUrl" class="flex items-center mb-2">
                    <img :src="coverImageUrl || coverImageObjectUrl" alt="Cover Preview" class="h-48 w-auto mr-2 object-contain border" />
                    <Button class="p-button-text p-button-sm" @click="clearCoverImage">
                      <Icon name="material-symbols-light:close" class="w-full" size="1.5em" />
                    </Button>
                  </div>

                  <FileUpload
                    mode="advanced"
                    accept="image/*"
                    :auto="true"
                    choose-label="Select Cover Image"
                    :multiple="false"
                    class="w-full cover-image-upload"
                    :custom-upload="true"
                    :show-upload-button="false"
                    :show-cancel-button="false"
                    drag-drop-text="Select Cover Image or Drag and Drop Here"
                    @select="handleCoverImageUpload"
                  />
                </div>
              </div>
            </div>

            <!-- Search section -->
            <div class="mt-6">
              <h3 class="text-lg font-medium mb-2">Search for Metadata</h3>
              <div class="mb-4 grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label class="block text-sm font-medium mb-1">Search Term</label>
                  <InputText v-model="searchQuery" class="w-full" placeholder="Search term..." />
                </div>
                <div>
                  <label class="block text-sm font-medium mb-1">Author (optional)</label>
                  <InputText v-model="authorQuery" class="w-full" placeholder="Author name..." />
                </div>
              </div>
              <div class="flex flex-wrap gap-2 mb-6">
                <template v-if="selectedMediaType === MediaType.Anime">
                  <Button label="AniList" @click="searchAPI('AnilistAnime')" />
                  <Button label="MyAnimeList" @click="searchAPI('JikanAnime')" />
                </template>
                <template v-else-if="selectedMediaType === MediaType.Manga">
                  <Button label="AniList" @click="searchAPI('AnilistManga')" />
                  <Button label="MyAnimeList" @click="searchAPI('JikanManga')" />
                </template>
                <template v-else-if="selectedMediaType === MediaType.Movie || selectedMediaType === MediaType.Drama">
                  <Button label="TMDB" @click="searchAPI('Tmdb')" />
                </template>
                <template v-else-if="selectedMediaType === MediaType.Novel || selectedMediaType === MediaType.NonFiction">
                  <Button label="Anilist" @click="searchAPI('AnilistNovel')" />
                  <Button label="MyAnimeList" @click="searchAPI('JikanNovel')" />
                  <Button label="Google Books" @click="searchAPI('GoogleBooks')" />
                  <Button label="Bookmeter" @click="searchAPI('Bookmeter')" />
                </template>
                <template v-else-if="selectedMediaType === MediaType.VideoGame">
                  <Button label="IGDB" @click="searchAPI('Igdb')" />
                </template>
                <template v-else-if="selectedMediaType === MediaType.VisualNovel">
                  <Button label="VNDB" @click="searchAPI('Vndb')" />
                </template>
                <template v-else-if="selectedMediaType === MediaType.WebNovel">
                  <Button label="Syosetsu" @click="searchAPI('Syosetsu')" />
                </template>
              </div>
            </div>
          </template>
        </Card>

        <!-- Subdecks section -->
        <div v-if="selectedFile" class="mt-6">
          <h3 class="text-lg font-medium mb-4">Subdecks</h3>
          <Card v-for="subdeck in subdecks" :key="subdeck.id" class="mb-4">
            <template #title>
              <div class="flex justify-between items-center">
                <InputText v-model="subdeck.originalTitle" class="w-64" />
                <Button
                  v-if="subdecks.length > 1 && subdeck.id === subdecks[subdecks.length - 1].id"
                  class="p-button-danger p-button-text"
                  @click="removeSubdeck(subdeck.id)"
                >
                  <Icon name="material-symbols-light:delete-forever" class="w-full" size="2em" />
                </Button>
              </div>
            </template>
            <template #content>
              <div v-if="!subdeck.file">
                <FileUpload
                  mode="advanced"
                  :auto="true"
                  choose-label="Select File"
                  :multiple="false"
                  class="w-full subdeck-file-upload"
                  :custom-upload="true"
                  :show-upload-button="false"
                  :show-cancel-button="false"
                  drag-drop-text="Select File or Drag and Drop Here"
                  @select="(e) => handleSubdeckFileUpload(e, subdeck.id)"
                />
              </div>
              <div v-else class="flex items-center">
                <span class="text-sm text-gray-600">{{ subdeck.file.name }}</span>
              </div>
            </template>
          </Card>
          <Card class="mb-4">
            <template #title>
              <div class="flex justify-between items-center">
                <span>Add New Subdeck</span>
              </div>
            </template>
            <template #content>
              <FileUpload
                ref="newSubdeckUploaderRef"
                mode="advanced"
                :auto="true"
                choose-label="Select File to Add Subdeck"
                :multiple="true"
                class="w-full subdeck-file-upload"
                :custom-upload="true"
                :show-upload-button="false"
                :show-cancel-button="false"
                drag-drop-text="Drag and drop here to add a new subdeck"
                @select="handleNewSubdeckFileUpload"
              >
                <template #empty>
                  <div class="flex items-center justify-center flex-col">
                    <Icon name="material-symbols-light:arrow-upload-progress" class="!border-2 !rounded-full !p-8 !text-4xl !text-muted-color" />
                    <p class="mt-6 mb-0">Drag and drop file to here to upload.</p>
                  </div>
                </template>
              </FileUpload>
            </template>
          </Card>
        </div>

        <div v-if="selectedFile" class="mt-6 flex justify-center">
          <Button label="Submit" class="p-button-lg p-button-success" :disabled="!originalTitle.trim() || (!coverImage && !coverImageUrl)" @click="submitMedia">
            <Icon name="material-symbols-light:check-circle" class="w-full" size="2em" />
            Submit
          </Button>
        </div>
      </div>

      <SearchDialog
        v-model:visible="showSearchResultsDialog"
        :query="searchQuery"
        :author="authorQuery"
        :provider="currentProvider"
        @select-metadata="handleSelectMetadata"
        @link-metadata="handleLinkMetadata"
      />
    </div>
  </div>
</template>

<style scoped>
  .p-fileupload.p-fileupload-advanced .p-fileupload-buttonbar {
    display: none;
  }

  .p-fileupload.p-fileupload-advanced .p-fileupload-content {
    margin-top: 0;
    padding-top: 20px;
    padding-bottom: 20px;
    min-height: 100px;
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: center;
    border: 2px dashed #ccc;
    border-radius: 4px;
    transition: all 0.3s ease;
    text-align: center;
  }

  .p-fileupload.p-fileupload-advanced .p-fileupload-content:hover {
    border-color: #6366f1; /* PrimeVue indigo-500 */
    background-color: rgba(99, 102, 241, 0.05);
  }

  .p-fileupload.p-fileupload-advanced .p-fileupload-content.p-fileupload-highlight {
    border-color: #6366f1;
    background-color: rgba(99, 102, 241, 0.1);
    box-shadow: 0 0 10px rgba(99, 102, 241, 0.3);
  }

  .p-fileupload.p-fileupload-advanced .p-fileupload-content .p-messages-icon,
  .p-fileupload.p-fileupload-advanced .p-fileupload-content .p-icon,
  .p-fileupload.p-fileupload-advanced .p-fileupload-content .pi-upload {
    font-size: 2rem; /* Make icon larger */
    margin-bottom: 0.5rem;
  }

  .p-fileupload.p-fileupload-advanced .p-fileupload-content > div > span[data-pc-section='dndmessage'] {
    font-weight: bold;
  }
</style>
