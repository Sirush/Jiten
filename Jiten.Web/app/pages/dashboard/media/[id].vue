<script setup lang="ts">
  import { ref, computed, watch, onBeforeUnmount, onMounted } from 'vue'; // Added onMounted
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import FileUpload from 'primevue/fileupload';
  import InputText from 'primevue/inputtext';
  import Dialog from 'primevue/dialog';
  import Toast from 'primevue/toast';
  import DataTable from 'primevue/datatable';
  import Column from 'primevue/column';
  import { useToast } from 'primevue/usetoast';
  import { DeckRelationshipType, LinkType } from '~/types';
  import type { Deck, DeckDetail, DeckRelationship, Link, MediaType, Tag, Genre } from '~/types';
  import { getMediaTypeText, getChildrenCountText } from '~/utils/mediaTypeMapper';
  import { getLinkTypeText } from '~/utils/linkTypeMapper';
  import { getAllGenres } from '~/utils/genreMapper';
  import Checkbox from 'primevue/checkbox';
  import Select from 'primevue/select';
  import MultiSelect from 'primevue/multiselect';
  import InputNumber from 'primevue/inputnumber';

  const route = useRoute();
  const mediaId = route.params.id;

  useHead({
    title: 'Edit Media - Admin Dashboard - Jiten',
  });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  const selectedMediaType = ref<MediaType | null>(null);
  const toast = useToast();
  const { $api } = useNuxtApp();

  function showToast(severity: 'success' | 'info' | 'warn' | 'error', summary: string, detail: string = '') {
    toast.add({ severity, summary, detail, life: 3000 });
  }

  const selectedFile = ref<File | null>(null);
  const parentTextFile = ref<File | null>(null);
  const originalTitle = ref('');
  const romajiTitle = ref('');
  const englishTitle = ref('');
  const releaseDate = ref<Date>();
  const description = ref('');
  const difficultyOverride = ref(0);
  const hideDialoguePercentage = ref(false);
  const hideAverageSentenceLength = ref(false);

  const coverImage = ref<File | null>(null);
  const coverImageUrl = ref<string | null>(null);
  const coverImageObjectUrl = ref<string | null>(null);

  const links = ref<Link[]>([]);
  const showAddLinkDialog = ref(false);
  const showEditLinkDialog = ref(false);
  const newLink = ref<{ url: string; linkType: LinkType }>({
    url: '',
    linkType: LinkType.Web,
  });
  const editingLink = ref<{ index: number; url: string; linkType: LinkType } | null>(null);

  // Aliases state
  const aliases = ref<string[]>([]);
  const showAddAliasDialog = ref(false);
  const newAlias = ref('');
  const aliasesExpanded = ref(false);
  const visibleAliases = computed(() => aliasesExpanded.value ? aliases.value : aliases.value.slice(0, 3));

  // Dictionary entries (character names)
  const dictionaryEntries = ref<string[]>([]);
  const showAddDictEntryDialog = ref(false);
  const newDictEntry = ref('');
  const dictEntriesExpanded = ref(false);
  const visibleDictEntries = computed(() => dictEntriesExpanded.value ? dictionaryEntries.value : dictionaryEntries.value.slice(0, 3));

  // Genres
  const selectedGenres = ref<number[]>([]);
  const genreOptions = computed(() => getAllGenres());

  // Tags
  const availableTags = ref<Tag[]>([]);
  const selectedTags = ref<Array<{ tagId: number; name: string; percentage: number }>>([]);
  const showAddTagDialog = ref(false);
  const newTag = ref<{ tagId: number | null; percentage: number }>({
    tagId: null,
    percentage: 50
  });
  const tagsLoading = ref(false);

  // Relationships
  const relationships = ref<Array<{
    targetDeckId: number;
    targetTitle: string;
    relationshipType: DeckRelationshipType;
    isInverse: boolean;
  }>>([]);
  const showAddRelationshipDialog = ref(false);
  const newRelationship = ref<{
    targetDeckId: number | null;
    targetTitle: string;
    relationshipType: DeckRelationshipType | null;
  }>({ targetDeckId: null, targetTitle: '', relationshipType: null });
  const fetchingDeckTitle = ref(false);
  const showRecomputeDifficultyDialog = ref(false);
  const showReaggregateDifficultyDialog = ref(false);

  const relationshipTypeOptions = [
    { label: 'Sequel', value: DeckRelationshipType.Sequel },
    { label: 'Fandisc', value: DeckRelationshipType.Fandisc },
    { label: 'Spinoff', value: DeckRelationshipType.Spinoff },
    { label: 'Side Story', value: DeckRelationshipType.SideStory },
    { label: 'Adaptation', value: DeckRelationshipType.Adaptation },
    { label: 'Alternative', value: DeckRelationshipType.Alternative },
  ];

  const relationshipTypeLabels: Record<DeckRelationshipType, string> = {
    [DeckRelationshipType.Prequel]: 'Prequel',
    [DeckRelationshipType.Sequel]: 'Sequel',
    [DeckRelationshipType.Fandisc]: 'Fandisc',
    [DeckRelationshipType.Spinoff]: 'Spinoff',
    [DeckRelationshipType.SideStory]: 'Side Story',
    [DeckRelationshipType.Adaptation]: 'Adaptation',
    [DeckRelationshipType.Alternative]: 'Alternative',
    [DeckRelationshipType.HasFandisc]: 'Has Fandisc',
    [DeckRelationshipType.HasSpinoff]: 'Has Spinoff',
    [DeckRelationshipType.HasSideStory]: 'Has Side Story',
    [DeckRelationshipType.SourceMaterial]: 'Source Material',
  };

  const newSubdeckUploaderRef = ref<InstanceType<typeof FileUpload> | null>(null);

  const availableLinkTypes = computed(() => {
    return Object.values(LinkType)
      .filter((value) => typeof value === 'number')
      .map((value) => ({
        value: value as LinkType,
        label: getLinkTypeText(value as LinkType),
      }));
  });

  const subdecks = ref<
    Array<{
      id: number;
      originalTitle: string;
      file: File | null;
      mediaSubdeckId?: number; // Added to track existing subdeck IDs
      difficultyOverride: number;
    }>
  >([]);
  let nextSubdeckId = 1;

  const subdeckDefaultName = computed(() => {
    if (!selectedMediaType.value) return '';

    const baseText = getChildrenCountText(selectedMediaType.value);
    const singularText = baseText.endsWith('s') ? baseText.slice(0, -1) : baseText;
    return singularText;
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
  });

  const { data: response, status, error } = await useApiFetch<DeckDetail>(`admin/deck/${mediaId}`, { server: false });

  watchEffect(() => {
    if (error.value) {
      throw new Error(error.value.message || 'Failed to fetch deck data');
    }

    if (response.value) {
      const mainDeck = response.value.mainDeck;

      selectedMediaType.value = mainDeck.mediaType;
      originalTitle.value = mainDeck.originalTitle || '';
      romajiTitle.value = mainDeck.romajiTitle || '';
      englishTitle.value = mainDeck.englishTitle || '';
      description.value = mainDeck.description || '';
      releaseDate.value = new Date(mainDeck.releaseDate) || new Date();
      difficultyOverride.value = mainDeck.difficultyOverride || 0;
      hideDialoguePercentage.value = mainDeck.hideDialoguePercentage || false;
      hideAverageSentenceLength.value = mainDeck.hideAverageSentenceLength || false;

      if (mainDeck.coverName) {
        coverImageUrl.value = `${mainDeck.coverName}`;
      }

      links.value = mainDeck.links || [];
      aliases.value = mainDeck.aliases || [];
      dictionaryEntries.value = mainDeck.dictionaryEntries?.map((e: any) => e.surface) || [];

      selectedGenres.value = mainDeck.genres || [];
      selectedTags.value = mainDeck.tags?.map(t => ({
        tagId: t.tagId,
        name: t.name,
        percentage: t.percentage
      })) || [];

      relationships.value = mainDeck.relationships?.map(r => ({
        targetDeckId: r.targetDeckId,
        targetTitle: r.targetTitle,
        relationshipType: r.relationshipType,
        isInverse: r.isInverse
      })) || [];

      loadAvailableTags();

      if (response.value.subDecks && response.value.subDecks.length > 0) {
        subdecks.value = response.value.subDecks.map((subdeck, index) => ({
          id: nextSubdeckId++,
          originalTitle: subdeck.originalTitle || `${subdeckDefaultName.value} ${index + 1}`,
          file: null,
          mediaSubdeckId: subdeck.deckId,
          difficultyOverride: subdeck.difficultyOverride || -1,
        }));
      }

      selectedFile.value = new File([], 'dummy.file');
    }
  });

  function handleCoverImageUpload(event: { files: File[] }) {
    if (event.files && event.files.length > 0) {
      const file = event.files[0];
      coverImage.value = file;
      coverImageUrl.value = null;
    }
  }

  function handleParentTextFileUpload(event: { files: File[] }) {
    if (event.files && event.files.length > 0) {
      parentTextFile.value = event.files[0];
    }
  }

  function handleSubdeckFileUpload(event: { files: File[] }, subdeckId: number) {
    if (event.files && event.files.length > 0) {
      for (const file of event.files) {
        const subdeck = subdecks.value.find((sd) => sd.id === subdeckId);
        if (subdeck) {
          subdeck.file = file;
        }
      }
    }
  }

  function handleNewSubdeckFileUpload(event: { files: File[] }) {
    if (event.files && event.files.length > 0) {
      for (const file of event.files) {
        const newSubdeckNumber = subdecks.value.length + 1;
        subdecks.value.push({
          id: nextSubdeckId++,
          originalTitle: `${subdeckDefaultName.value} ${newSubdeckNumber}`,
          file: file,
          difficultyOverride: -1,
        });
      }
      // Explicitly clear the FileUpload component's selection
      if (newSubdeckUploaderRef.value) {
        newSubdeckUploaderRef.value.clear();
      }
    }
  }

  function addSubdeck() {
    const newSubdeckNumber = subdecks.value.length + 1;
    subdecks.value.push({
      id: nextSubdeckId++,
      originalTitle: `${subdeckDefaultName.value} ${newSubdeckNumber}`,
      file: null,
      difficultyOverride: -1,
    });
  }

  function removeSubdeck(id: number) {
    const index = subdecks.value.findIndex((sd) => sd.id === id);
    if (index === -1) return;

    const subdeck = subdecks.value[index];
    if (subdeck.mediaSubdeckId) {
      if (!confirm('Are you sure you want to remove this subdeck? This action cannot be undone.')) {
        return;
      }
    }

    subdecks.value.splice(index, 1);
  }

  function moveSubdeckUp(id: number) {
    const index = subdecks.value.findIndex((sd) => sd.id === id);
    if (index <= 0) return;

    const newSubdecks = [...subdecks.value];
    const temp = newSubdecks[index];
    newSubdecks[index] = newSubdecks[index - 1];
    newSubdecks[index - 1] = temp;
    subdecks.value = newSubdecks;
  }

  function moveSubdeckDown(id: number) {
    const index = subdecks.value.findIndex((sd) => sd.id === id);
    if (index === -1 || index >= subdecks.value.length - 1) return;

    const newSubdecks = [...subdecks.value];
    const temp = newSubdecks[index];
    newSubdecks[index] = newSubdecks[index + 1];
    newSubdecks[index + 1] = temp;
    subdecks.value = newSubdecks;
  }

  async function loadAvailableTags() {
    tagsLoading.value = true;
    try {
      availableTags.value = await $api<Tag[]>('admin/tags');
    } catch (error) {
      showToast('error', 'Error', 'Failed to load tags');
      console.error('Error loading tags:', error);
    } finally {
      tagsLoading.value = false;
    }
  }

  function openAddTagDialog() {
    newTag.value = { tagId: null, percentage: 50 };
    showAddTagDialog.value = true;
  }

  function addTag() {
    if (!newTag.value.tagId) {
      showToast('warn', 'Validation', 'Please select a tag');
      return;
    }

    const exists = selectedTags.value.some(t => t.tagId === newTag.value.tagId);
    if (exists) {
      showToast('warn', 'Duplicate', 'Tag already added');
      return;
    }

    const tag = availableTags.value.find(t => t.tagId === newTag.value.tagId);
    if (tag) {
      selectedTags.value.push({
        tagId: newTag.value.tagId!,
        name: tag.name,
        percentage: newTag.value.percentage
      });
    }

    showAddTagDialog.value = false;
  }

  function removeTag(index: number) {
    selectedTags.value.splice(index, 1);
  }

  // Relationship functions
  async function fetchDeckTitle() {
    if (!newRelationship.value.targetDeckId) {
      showToast('warn', 'Validation', 'Please enter a deck ID');
      return;
    }

    if (newRelationship.value.targetDeckId === parseInt(mediaId as string)) {
      showToast('warn', 'Validation', 'Cannot add a relationship to itself');
      newRelationship.value.targetTitle = '';
      return;
    }

    fetchingDeckTitle.value = true;
    try {
      const result = await $api<{ mainDeck: { originalTitle: string } }>(`admin/deck/${newRelationship.value.targetDeckId}`);
      newRelationship.value.targetTitle = result.mainDeck.originalTitle;
    } catch (error) {
      showToast('error', 'Error', 'Deck not found');
      newRelationship.value.targetTitle = '';
    } finally {
      fetchingDeckTitle.value = false;
    }
  }

  function openAddRelationshipDialog() {
    newRelationship.value = { targetDeckId: null, targetTitle: '', relationshipType: null };
    showAddRelationshipDialog.value = true;
  }

  function addRelationship() {
    if (!newRelationship.value.targetDeckId || !newRelationship.value.relationshipType) {
      showToast('warn', 'Validation', 'Please select a deck and relationship type');
      return;
    }

    if (!newRelationship.value.targetTitle) {
      showToast('warn', 'Validation', 'Please verify the deck ID first');
      return;
    }

    const exists = relationships.value.some(
      r => r.targetDeckId === newRelationship.value.targetDeckId &&
           r.relationshipType === newRelationship.value.relationshipType
    );
    if (exists) {
      showToast('warn', 'Duplicate', 'This relationship already exists');
      return;
    }

    relationships.value.push({
      targetDeckId: newRelationship.value.targetDeckId,
      targetTitle: newRelationship.value.targetTitle,
      relationshipType: newRelationship.value.relationshipType,
      isInverse: false
    });
    showAddRelationshipDialog.value = false;
  }

  function removeRelationship(index: number) {
    relationships.value.splice(index, 1);
  }

  function getRelationshipTypeLabel(type: DeckRelationshipType): string {
    return relationshipTypeLabels[type] ?? 'Unknown';
  }

  function moveSubdeckToPosition(id: number, targetPosition: number | null) {
    if (targetPosition === null) return;

    const currentIndex = subdecks.value.findIndex((sd) => sd.id === id);
    if (currentIndex === -1) return;

    const targetIndex = targetPosition - 1;
    if (targetIndex < 0 || targetIndex >= subdecks.value.length || targetIndex === currentIndex) return;

    const newSubdecks = [...subdecks.value];
    const [subdeck] = newSubdecks.splice(currentIndex, 1);
    newSubdecks.splice(targetIndex, 0, subdeck);
    subdecks.value = newSubdecks;
  }

  function openAddLinkDialog() {
    newLink.value = {
      url: '',
      linkType: LinkType.Web,
    };
    showAddLinkDialog.value = true;
  }

  function addLink() {
    if (!newLink.value.url.trim()) {
      showToast('warn', 'Validation Error', 'URL is required');
      return;
    }

    links.value.push({
      linkId: 0,
      url: newLink.value.url,
      linkType: newLink.value.linkType.toString(),
      deckId: parseInt(mediaId as string),
    });

    showAddLinkDialog.value = false;
  }

  function openEditLinkDialog(index: number) {
    const link = links.value[index];
    editingLink.value = {
      index,
      url: link.url,
      linkType: parseInt(link.linkType) as LinkType,
    };
    showEditLinkDialog.value = true;
  }

  function saveEditedLink() {
    if (!editingLink.value || !editingLink.value.url.trim()) {
      showToast('warn', 'Validation Error', 'URL is required');
      return;
    }

    const index = editingLink.value.index;
    links.value[index] = {
      ...links.value[index],
      url: editingLink.value.url,
      linkType: editingLink.value.linkType.toString(),
    };

    showEditLinkDialog.value = false;
    editingLink.value = null;
  }

  function removeLink(index: number) {
    links.value.splice(index, 1);
  }

  // Aliases handlers
  function openAddAliasDialog() {
    newAlias.value = '';
    showAddAliasDialog.value = true;
  }

  function addAlias() {
    if (!newAlias.value.trim()) {
      showToast('warn', 'Validation Error', 'Alias is required');
      return;
    }
    aliases.value.push(newAlias.value.trim());
    showAddAliasDialog.value = false;
  }

  function removeAlias(index: number) {
    aliases.value.splice(index, 1);
  }

  // Dictionary entries handlers
  function openAddDictEntryDialog() {
    newDictEntry.value = '';
    showAddDictEntryDialog.value = true;
  }

  function addDictEntry() {
    if (!newDictEntry.value.trim()) {
      showToast('warn', 'Validation Error', 'Name is required');
      return;
    }
    dictionaryEntries.value.push(newDictEntry.value.trim());
    showAddDictEntryDialog.value = false;
  }

  function removeDictEntry(index: number) {
    dictionaryEntries.value.splice(index, 1);
  }

  async function fetchMetadata() {
    try {
      const data = await $api('admin/fetch-metadata/' + mediaId, {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Fetching metadata has been queued',
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to fetch metadata',
        life: 5000,
      });
      console.error('Error fetching metadata:', error);
    } finally {
    }
  }

  async function recomputeDifficulty() {
    try {
      const data = await $api<{ count: number }>(`/admin/recompute-difficulty/${mediaId}`, {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Queued difficulty computation for ${data.count} deck(s)`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to queue difficulty recomputation',
        life: 5000,
      });
      console.error('Error recomputing difficulty:', error);
    }
  }

  async function reaggregateParentDifficulty() {
    try {
      await $api(`/admin/reaggregate-parent-difficulty/${mediaId}`, {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Queued difficulty reaggregation from children',
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to queue difficulty reaggregation',
        life: 5000,
      });
      console.error('Error reaggregating difficulty:', error);
    }
  }

  async function update(reparse: boolean = false) {
    if (!originalTitle.value.trim()) {
      showToast('warn', 'Validation Error', 'Original title is required');
      return;
    }

    if (!coverImage.value && !coverImageUrl.value) {
      showToast('warn', 'Validation Error', 'Cover image is required');
      return;
    }

    try {
      const formData = new FormData();
      formData.append('reparse', reparse.toString());
      formData.append('deckId', mediaId.toString());
      formData.append('mediaType', selectedMediaType.value?.toString() || '');
      formData.append('originalTitle', originalTitle.value);
      formData.append('romajiTitle', romajiTitle.value);
      formData.append('englishTitle', englishTitle.value);
      formData.append('releaseDate', formatDateAsYyyyMmDd(releaseDate.value));
      formData.append('description', description.value);
      formData.append('difficultyOverride', difficultyOverride.value);
      formData.append('hideDialoguePercentage', hideDialoguePercentage.value);
      formData.append('hideAverageSentenceLength', hideAverageSentenceLength.value);

      if (coverImage.value) {
        formData.append('coverImage', coverImage.value);
      } else if (coverImageUrl.value) {
        formData.append('coverImageUrl', coverImageUrl.value);
      }

      if (parentTextFile.value) {
        formData.append('file', parentTextFile.value);
      }

      if (links.value && links.value.length > 0) {
        for (let i = 0; i < links.value.length; i++) {
          const link = links.value[i];
          formData.append(`links[${i}].url`, link.url);
          formData.append(`links[${i}].linkType`, link.linkType);
          if (link.linkId > 0) {
            formData.append(`links[${i}].linkId`, link.linkId.toString());
          }
        }
      }

      if (aliases.value && aliases.value.length > 0) {
        for (let i = 0; i < aliases.value.length; i++) {
          formData.append(`aliases[${i}]`, aliases.value[i]);
        }
      }

      if (dictionaryEntries.value && dictionaryEntries.value.length > 0) {
        for (let i = 0; i < dictionaryEntries.value.length; i++) {
          formData.append(`dictionaryEntries[${i}]`, dictionaryEntries.value[i]);
        }
      }

      // Add genres
      selectedGenres.value.forEach((genre, index) => {
        formData.append(`genres[${index}]`, genre.toString());
      });

      // Add tags
      selectedTags.value.forEach((tag, index) => {
        formData.append(`tags[${index}].tagId`, tag.tagId.toString());
        formData.append(`tags[${index}].percentage`, tag.percentage.toString());
      });

      // Add relationships (only direct, not inverse - inverse are computed from other deck's data)
      const directRelationships = relationships.value.filter(r => !r.isInverse);
      directRelationships.forEach((rel, index) => {
        formData.append(`relationships[${index}].targetDeckId`, rel.targetDeckId.toString());
        formData.append(`relationships[${index}].relationshipType`, rel.relationshipType.toString());
      });

      if (subdecks.value.length > 0) {
        for (let i = 0; i < subdecks.value.length; i++) {
          const subdeck = subdecks.value[i];
          formData.append(`subdecks[${i}].originalTitle`, subdeck.originalTitle);

          // Include the existing subdeck ID if available
          if (subdeck.mediaSubdeckId) {
            formData.append(`subdecks[${i}].deckId`, subdeck.mediaSubdeckId.toString());
          }

          formData.append(`subdecks[${i}].deckOrder`, (i + 1).toString());
          formData.append(`subdecks[${i}].difficultyOverride`, subdeck.difficultyOverride.toString());

          // Include the file if available
          if (subdeck.file) {
            formData.append(`subdecks[${i}].file`, subdeck.file);
          }
        }
      }

      const data = await $api('admin/update-deck', {
        method: 'POST',
        body: formData,
      });

      showToast('success', 'Success', 'Media updated successfully!');
    } catch (error) {
      console.error('Error updating media:', error);
      showToast('error', 'Update Error', 'An error occurred while updating. Please try again.');
    }
  }
</script>

<template>
  <div>
    <div class="container mx-auto p-4">
      <div class="flex items-center mb-6">
        <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
        <h1 class="text-3xl font-bold">Edit Media</h1>
        <div class="ml-auto">
          <Button @click="navigateTo(`/decks/media/${mediaId}/detail`)">
            <Icon name="ic:baseline-remove-red-eye" />
            View Deck
          </Button>
        </div>
      </div>

      <!-- Loading indicator -->
      <div v-if="status == 'pending'" class="flex justify-center items-center h-64">
        <div class="text-center">
          <div class="spinner-border text-primary" role="status">
            <span class="sr-only">Loading...</span>
          </div>
          <p class="mt-2">Loading media data...</p>
        </div>
      </div>

      <!-- File Upload Screen -->
      <div v-else class="mt-6">
        <div class="flex items-center mb-4">
          <h2 class="text-xl font-semibold">Edit {{ getMediaTypeText(selectedMediaType!) }}</h2>
        </div>

        <!-- File details card -->
        <Card class="mb-6">
          <template #title>Media Details</template>
          <template #content>
            <div class="grid grid-cols-1 md:grid-cols-2 gap-6">
              <div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Original Title</label>
                  <InputText v-model="originalTitle" class="w-full" />
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Romaji Title</label>
                  <InputText v-model="romajiTitle" class="w-full" />
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">English Title</label>
                  <InputText v-model="englishTitle" class="w-full" />
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Release Date</label>
                  <DatePicker v-model="releaseDate" class="w-full" />
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Description</label>
                  <Textarea v-model="description" class="w-full" :rows="6" />
                </div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Difficulty Override</label>
                  <InputNumber v-model="difficultyOverride" class="w-full" :min-fraction-digits="1" />
                </div>

                <div class="flex items-center">
                  <Checkbox id="hideDialoguePercentage" v-model="hideDialoguePercentage" :binary="true" />
                  <label for="hideDialoguePercentage" class="ml-2">Hide Dialogue Percentage</label>
                </div>
                <div class="flex items-center mt-2">
                  <Checkbox id="hideAverageSentenceLength" v-model="hideAverageSentenceLength" :binary="true" />
                  <label for="hideAverageSentenceLength" class="ml-2">Hide Average Sentence Length</label>
                </div>
              </div>
              <div>
                <div class="mb-4">
                  <label class="block text-sm font-medium mb-1">Cover Image</label>
                  <!-- Show image preview if available (either from URL or local file) -->
                  <div v-if="coverImageUrl || coverImageObjectUrl" class="flex items-center mb-2">
                    <img :src="coverImageUrl || coverImageObjectUrl" alt="Cover Preview" class="h-48 w-auto mr-2 object-contain border" />
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

            <!-- Deck Information Table -->
            <div v-if="response && response.mainDeck" class="mt-6">
              <h3 class="text-lg font-medium mb-2">Deck Information</h3>
              <div class="p-4 border rounded mb-4">
                <div class="grid grid-cols-2 gap-4">
                  <div>
                    <p><strong>Media Type:</strong> {{ getMediaTypeText(response.mainDeck.mediaType) }}</p>
                    <p><strong>Deck ID:</strong> {{ response.mainDeck.deckId }}</p>
                    <p><strong>Word Count:</strong> {{ response.mainDeck.wordCount }}</p>
                    <p><strong>Unique Words:</strong> {{ response.mainDeck.uniqueWordCount }}</p>
                  </div>
                  <div>
                    <p><strong>Character Count:</strong> {{ response.mainDeck.characterCount }}</p>
                    <p><strong>Unique Kanji:</strong> {{ response.mainDeck.uniqueKanjiCount }}</p>
                    <p><strong>Difficulty:</strong> {{ response.mainDeck.difficultyRaw.toFixed(2) }}</p>
                    <p><strong>Avg. Sentence Length:</strong> {{ response.mainDeck.averageSentenceLength.toFixed(2) }}</p>
                  </div>
                </div>
              </div>
            </div>

            <!-- Links Section -->
            <div class="mt-6">
              <div class="flex justify-between items-center mb-2">
                <h3 class="text-lg font-medium">Links</h3>
                <Button @click="openAddLinkDialog">
                  <Icon name="material-symbols-light:add-circle-outline" size="1.5em" />
                  Add Link
                </Button>
              </div>

              <div v-if="links.length === 0" class="p-4 border rounded text-center text-gray-500">No links available. Click "Add Link" to add one.</div>

              <div v-else class="mb-4">
                <ul class="list-none p-0">
                  <li v-for="(link, index) in links" :key="index" class="flex justify-between items-center p-2 border-b">
                    <div>
                      <span class="font-medium">{{ getLinkTypeText(parseInt(link.linkType)) }}:</span>
                      <a :href="link.url" target="_blank" class="ml-2 text-blue-500 hover:underline">{{ link.url }}</a>
                    </div>
                    <div class="flex">
                      <Button class="p-button-text p-button-info" @click="openEditLinkDialog(index)">
                        <Icon name="material-symbols-light:edit" size="1.5em" />
                      </Button>
                      <Button class="p-button-text p-button-danger" @click="removeLink(index)">
                        <Icon name="material-symbols-light:delete" size="1.5em" />
                      </Button>
                    </div>
                  </li>
                </ul>
              </div>

              <!-- Add Link Dialog -->
              <Dialog v-model:visible="showAddLinkDialog" header="Add Link" :modal="true" class="w-full md:w-1/2">
                <div class="p-fluid">
                  <div class="mb-4">
                    <label class="block text-sm font-medium mb-1">Link Type</label>
                    <Select
                      v-model="newLink.linkType"
                      :options="availableLinkTypes"
                      option-label="label"
                      option-value="value"
                      placeholder="Select Link Type"
                      class="w-full"
                    />
                  </div>
                  <div class="mb-4">
                    <label class="block text-sm font-medium mb-1">URL</label>
                    <InputText v-model="newLink.url" placeholder="Enter URL" class="w-full" />
                  </div>
                </div>
                <template #footer>
                  <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showAddLinkDialog = false" />
                  <Button label="Add" icon="pi pi-check" class="p-button-text" @click="addLink" />
                </template>
              </Dialog>

              <!-- Edit Link Dialog -->
              <Dialog v-model:visible="showEditLinkDialog" header="Edit Link" :modal="true" class="w-full md:w-1/2">
                <div v-if="editingLink" class="p-fluid">
                  <div class="mb-4">
                    <label class="block text-sm font-medium mb-1">Link Type</label>
                    <Dropdown
                      v-model="editingLink.linkType"
                      :options="availableLinkTypes"
                      option-label="label"
                      option-value="value"
                      placeholder="Select Link Type"
                      class="w-full"
                    />
                  </div>
                  <div class="mb-4">
                    <label class="block text-sm font-medium mb-1">URL</label>
                    <InputText v-model="editingLink.url" placeholder="Enter URL" class="w-full" />
                  </div>
                </div>
                <template #footer>
                  <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showEditLinkDialog = false" />
                  <Button label="Save" icon="pi pi-check" class="p-button-text" @click="saveEditedLink" />
                </template>
              </Dialog>
            </div>

            <!-- Aliases Section -->
            <div class="mt-6">
              <div class="flex justify-between items-center mb-2">
                <h3 class="text-lg font-medium">Aliases</h3>
                <Button @click="openAddAliasDialog">
                  <Icon name="material-symbols-light:add-circle-outline" size="1.5em" />
                  Add Alias
                </Button>
              </div>

              <div v-if="aliases.length === 0" class="p-4 border rounded text-center text-gray-500">No aliases available. Click "Add Alias" to add one.</div>

              <div v-else class="mb-4">
                <ul class="list-none p-0">
                  <li v-for="(alias, index) in visibleAliases" :key="index" class="flex justify-between items-center p-2 border-b">
                    <div>{{ alias }}</div>
                    <div class="flex">
                      <Button class="p-button-text p-button-danger" @click="removeAlias(index)">
                        <Icon name="material-symbols-light:delete" size="1.5em" />
                      </Button>
                    </div>
                  </li>
                </ul>
                <Button v-if="aliases.length > 3" text size="small" class="mt-2" @click="aliasesExpanded = !aliasesExpanded">
                  {{ aliasesExpanded ? 'Show less' : `Show all (${aliases.length})` }}
                </Button>
              </div>

              <!-- Add Alias Dialog -->
              <Dialog v-model:visible="showAddAliasDialog" header="Add Alias" :modal="true" class="w-full md:w-1/2">
                <div class="p-fluid">
                  <div class="mb-4">
                    <label class="block text-sm font-medium mb-1">Alias</label>
                    <InputText v-model="newAlias" placeholder="Enter alias" class="w-full" />
                  </div>
                </div>
                <template #footer>
                  <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showAddAliasDialog = false" />
                  <Button label="Add" icon="pi pi-check" class="p-button-text" @click="addAlias" />
                </template>
              </Dialog>
            </div>

            <!-- Dictionary Entries Section (Character Names) -->
            <div class="mt-6">
              <div class="flex justify-between items-center mb-2">
                <h3 class="text-lg font-medium">Character Names</h3>
                <Button @click="openAddDictEntryDialog">
                  <Icon name="material-symbols-light:add-circle-outline" size="1.5em" />
                  Add Name
                </Button>
              </div>

              <div v-if="dictionaryEntries.length === 0" class="p-4 border rounded text-center text-gray-500">No character names. Click "Add Name" to add one, or fetch metadata to populate automatically.</div>

              <div v-else class="mb-4">
                <ul class="list-none p-0">
                  <li v-for="(entry, index) in visibleDictEntries" :key="index" class="flex justify-between items-center p-2 border-b">
                    <div>{{ entry }}</div>
                    <div class="flex">
                      <Button class="p-button-text p-button-danger" @click="removeDictEntry(index)">
                        <Icon name="material-symbols-light:delete" size="1.5em" />
                      </Button>
                    </div>
                  </li>
                </ul>
                <Button v-if="dictionaryEntries.length > 3" text size="small" class="mt-2" @click="dictEntriesExpanded = !dictEntriesExpanded">
                  {{ dictEntriesExpanded ? 'Show less' : `Show all (${dictionaryEntries.length})` }}
                </Button>
              </div>

              <Dialog v-model:visible="showAddDictEntryDialog" header="Add Character Name" :modal="true" class="w-full md:w-1/2">
                <div class="p-fluid">
                  <div class="mb-4">
                    <label class="block text-sm font-medium mb-1">Name</label>
                    <InputText v-model="newDictEntry" placeholder="Enter character name" class="w-full" />
                  </div>
                </div>
                <template #footer>
                  <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showAddDictEntryDialog = false" />
                  <Button label="Add" icon="pi pi-check" class="p-button-text" @click="addDictEntry" />
                </template>
              </Dialog>
            </div>
          </template>
        </Card>

        <!-- Genres Section -->
        <Card class="mt-6">
          <template #title>Genres</template>
          <template #content>
            <div class="mb-4">
              <label class="block text-sm font-medium mb-2">Select Genres</label>
              <MultiSelect
                v-model="selectedGenres"
                :options="genreOptions"
                option-label="label"
                option-value="value"
                placeholder="Select genres"
                class="w-full"
                :max-selected-labels="3"
              />
            </div>

            <div v-if="selectedGenres.length > 0" class="flex flex-wrap gap-2">
              <span
                v-for="genre in selectedGenres"
                :key="genre"
                class="inline-flex items-center gap-2 px-3 py-1.5 bg-purple-600 text-white rounded-full text-sm"
              >
                {{ genreOptions.find(g => g.value === genre)?.label }}
                <button
                  type="button"
                  @click="selectedGenres = selectedGenres.filter(g => g !== genre)"
                  class="hover:opacity-75"
                >
                  <Icon name="material-symbols-light:close" size="1em" />
                </button>
              </span>
            </div>
          </template>
        </Card>

        <!-- Tags Section -->
        <Card class="mt-6">
          <template #title>
            <div class="flex justify-between items-center">
              <span>Tags</span>
              <Button @click="openAddTagDialog" :loading="tagsLoading" size="small">
                <Icon name="material-symbols-light:add-circle-outline" size="1.25em" class="mr-1" />
                Add Tag
              </Button>
            </div>
          </template>
          <template #content>
            <div v-if="selectedTags.length === 0" class="text-center text-gray-500 py-4">
              No tags added. Click "Add Tag" to add one.
            </div>

            <ul v-else class="list-none p-0 space-y-2">
              <li
                v-for="(tag, index) in selectedTags"
                :key="tag.tagId"
                class="flex justify-between items-center p-3 border rounded"
              >
                <div class="flex items-center gap-4 flex-1">
                  <span class="font-medium min-w-[150px]">{{ tag.name }}</span>
                  <div class="flex items-center gap-2">
                    <label class="text-sm font-medium">Percentage:</label>
                    <InputNumber
                      v-model="selectedTags[index].percentage"
                      :min="0"
                      :max="100"
                      suffix="%"
                      class="w-24"
                    />
                  </div>
                </div>
                <Button severity="danger" text @click="removeTag(index)">
                  <Icon name="material-symbols-light:delete" size="1.5em" />
                </Button>
              </li>
            </ul>
          </template>
        </Card>

        <!-- Add Tag Dialog -->
        <Dialog v-model:visible="showAddTagDialog" header="Add Tag" :modal="true" class="w-full md:w-1/2">
          <div class="p-fluid">
            <div class="mb-4">
              <label class="block text-sm font-medium mb-2">Tag</label>
              <Select
                v-model="newTag.tagId"
                :options="availableTags"
                option-label="name"
                option-value="tagId"
                placeholder="Select a tag"
                class="w-full"
                :loading="tagsLoading"
              />
            </div>
            <div class="mb-4">
              <label class="block text-sm font-medium mb-2">Percentage</label>
              <InputNumber
                v-model="newTag.percentage"
                :min="0"
                :max="100"
                suffix="%"
                class="w-full"
              />
            </div>
          </div>
          <template #footer>
            <Button label="Cancel" severity="secondary" text @click="showAddTagDialog = false" />
            <Button label="Add" @click="addTag" />
          </template>
        </Dialog>

        <!-- Relationships Section -->
        <Card class="mt-6">
          <template #title>
            <div class="flex justify-between items-center">
              <span>Related Media</span>
              <Button @click="openAddRelationshipDialog" size="small">
                <Icon name="material-symbols-light:add-circle-outline" size="1.25em" class="mr-1" />
                Add Relationship
              </Button>
            </div>
          </template>
          <template #content>
            <div v-if="relationships.length === 0" class="text-center text-gray-500 py-4">
              No relationships defined. Click "Add Relationship" to link related media.
            </div>

            <ul v-else class="list-none p-0 space-y-2">
              <li
                v-for="(rel, index) in relationships"
                :key="`${rel.targetDeckId}-${rel.relationshipType}-${rel.isInverse}`"
                class="flex justify-between items-center p-3 border rounded"
                :class="{ 'opacity-75': rel.isInverse }"
              >
                <div>
                  <span class="font-medium">{{ getRelationshipTypeLabel(rel.relationshipType) }}:</span>
                  <NuxtLink :to="`/decks/media/${rel.targetDeckId}/detail`" class="ml-2 text-primary hover:underline">
                    {{ rel.targetTitle }}
                  </NuxtLink>
                  <span v-if="rel.isInverse" class="ml-2 text-xs text-gray-500">(inverse)</span>
                </div>
                <Button v-if="!rel.isInverse" severity="danger" text @click="removeRelationship(index)">
                  <Icon name="material-symbols-light:delete" size="1.5em" />
                </Button>
                <span v-else class="text-xs text-gray-400 italic">Edit on target deck</span>
              </li>
            </ul>
          </template>
        </Card>

        <!-- Recompute Difficulty Confirmation Dialog -->
        <Dialog v-model:visible="showRecomputeDifficultyDialog" header="Confirm Recompute Difficulty" :modal="true" class="w-full md:w-96">
          <p>Are you sure you want to recompute the difficulty for this deck?</p>
          <p class="text-sm text-gray-500 mt-2">This will queue a background job to recalculate difficulty scores using the RunPod API.</p>
          <template #footer>
            <Button label="Cancel" severity="secondary" text @click="showRecomputeDifficultyDialog = false" />
            <Button label="Recompute" @click="recomputeDifficulty(); showRecomputeDifficultyDialog = false" />
          </template>
        </Dialog>

        <!-- Reaggregate Parent Difficulty Confirmation Dialog -->
        <Dialog v-model:visible="showReaggregateDifficultyDialog" header="Confirm Reaggregate Difficulty" :modal="true" class="w-full md:w-96">
          <p>Are you sure you want to reaggregate the difficulty for this deck from its children?</p>
          <p class="text-sm text-gray-500 mt-2">This will recalculate the parent difficulty using the existing children's difficulty values without calling the external API.</p>
          <template #footer>
            <Button label="Cancel" severity="secondary" text @click="showReaggregateDifficultyDialog = false" />
            <Button label="Reaggregate" @click="reaggregateParentDifficulty(); showReaggregateDifficultyDialog = false" />
          </template>
        </Dialog>

        <!-- Add Relationship Dialog -->
        <Dialog v-model:visible="showAddRelationshipDialog" header="Add Relationship" :modal="true" class="w-full md:w-1/2">
          <div class="p-fluid">
            <div class="mb-4">
              <label class="block text-sm font-medium mb-2">Relationship Type</label>
              <Select
                v-model="newRelationship.relationshipType"
                :options="relationshipTypeOptions"
                option-label="label"
                option-value="value"
                placeholder="Select relationship type"
                class="w-full"
              />
            </div>
            <div class="mb-4">
              <label class="block text-sm font-medium mb-2">Target Deck ID</label>
              <div class="flex gap-2">
                <InputNumber
                  v-model="newRelationship.targetDeckId"
                  placeholder="Enter deck ID"
                  class="flex-1"
                  :use-grouping="false"
                />
                <Button @click="fetchDeckTitle" :loading="fetchingDeckTitle" :disabled="!newRelationship.targetDeckId">
                  Verify
                </Button>
              </div>
              <div v-if="newRelationship.targetTitle" class="mt-2 p-2 bg-surface-100 dark:bg-surface-800 rounded">
                Deck: <strong>{{ newRelationship.targetTitle }}</strong>
              </div>
            </div>
          </div>
          <template #footer>
            <Button label="Cancel" severity="secondary" text @click="showAddRelationshipDialog = false" />
            <Button label="Add" @click="addRelationship" :disabled="!newRelationship.targetDeckId || !newRelationship.relationshipType || !newRelationship.targetTitle" />
          </template>
        </Dialog>

        <!-- Replace text file (parent decks without children) -->
        <div v-if="!response?.subDecks?.length && !subdecks.length" class="mt-6">
          <h3 class="text-lg font-medium mb-4">Text File</h3>
          <Card>
            <template #content>
              <div v-if="parentTextFile" class="flex items-center gap-2">
                <span class="text-sm text-gray-600">{{ parentTextFile.name }}</span>
                <Button class="p-button-danger p-button-text p-button-sm" @click="parentTextFile = null">
                  <Icon name="material-symbols-light:close" size="1.2em" />
                </Button>
              </div>
              <FileUpload
                v-else
                mode="advanced"
                :auto="true"
                choose-label="Replace current text file"
                :multiple="false"
                class="w-full subdeck-file-upload"
                :custom-upload="true"
                :show-upload-button="false"
                :show-cancel-button="false"
                @select="handleParentTextFileUpload"
              />
            </template>
          </Card>
        </div>

        <!-- Subdecks section -->
        <div class="mt-6">
          <div class="flex justify-between items-center mb-4">
            <h3 class="text-lg font-medium">Subdecks</h3>
            <Button @click="addSubdeck">
              <Icon name="material-symbols-light:add-circle-outline" size="1.5em" />
              Add Subdeck
            </Button>
          </div>

          <div v-if="response && response.subDecks && response.subDecks.length > 0" class="mb-4">
            <DataTable :value="response.subDecks" class="p-datatable-sm">
              <Column field="deckId" header="ID" :sortable="true" />
              <Column field="originalTitle" header="Title" :sortable="true" />
              <Column field="characterCount" header="Chars" :sortable="true" />
              <Column field="wordCount" header="Words" :sortable="true" />
              <Column field="uniqueWordCount" header="Unique Words" :sortable="true" />
              <Column field="difficultyRaw" header="Difficulty" :sortable="true" />
              <Column header="Actions">
                <template #body="slotProps">
                  <Button class="p-button-text p-button-sm" @click="navigateTo(`/dashboard/media/${slotProps.data.deckId}`)">
                    <Icon name="material-symbols-light:edit" size="1.5em" />
                  </Button>
                </template>
              </Column>
            </DataTable>
          </div>

          <TransitionGroup name="subdeck-list" tag="div">
            <Card v-for="(subdeck, index) in subdecks" :key="subdeck.id" class="mb-4 subdeck-card">
            <template #title>
              <div class="flex flex-col gap-3 w-full">
                <div class="flex items-center justify-between w-full">
                  <div class="flex items-center gap-3">
                    <div class="flex flex-col gap-1">
                      <Button
                        class="p-button-text p-button-sm h-6"
                        :disabled="index === 0"
                        @click="moveSubdeckUp(subdeck.id)"
                        title="Move up"
                      >
                        <Icon name="material-symbols-light:arrow-upward" size="1.2em" />
                      </Button>
                      <Button
                        class="p-button-text p-button-sm h-6"
                        :disabled="index === subdecks.length - 1"
                        @click="moveSubdeckDown(subdeck.id)"
                        title="Move down"
                      >
                        <Icon name="material-symbols-light:arrow-downward" size="1.2em" />
                      </Button>
                    </div>
                    <div class="flex items-center gap-2">
                      <span class="text-lg font-semibold text-muted-color min-w-8">#{{ index + 1 }}</span>
                      <div class="flex flex-col">
                        <label class="block text-xs font-medium mb-1">Position</label>
                        <InputNumber
                          :model-value="index + 1"
                          :min="1"
                          :max="subdecks.length"
                          :step="1"
                          class="w-16"
                          :allow-empty="false"
                          @update:model-value="(val) => moveSubdeckToPosition(subdeck.id, val)"
                          title="Jump to position"
                        />
                      </div>
                    </div>
                  </div>
                  <Button class="p-button-danger p-button-text" icon-class="text-2xl" @click="removeSubdeck(subdeck.id)">
                    <Icon name="material-symbols-light:delete" size="1.5em" />
                  </Button>
                </div>
                <div class="flex flex-row gap-4">
                  <div>
                    <label class="block text-sm font-medium mb-1">Title</label>
                    <InputText v-model="subdeck.originalTitle" class="w-96" />
                  </div>
                  <div>
                    <label class="block text-sm font-medium mb-1">Difficulty Override</label>
                    <InputNumber v-model="subdeck.difficultyOverride" class="w-32" :min-fraction-digits="1" />
                  </div>
                </div>
              </div>
            </template>
            <template #content>
              <div v-if="!subdeck.file && !subdeck.mediaSubdeckId">
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
              <div v-else-if="subdeck.file" class="flex items-center">
                <span class="text-sm text-gray-600">{{ subdeck.file.name }}</span>
              </div>
              <div v-else-if="subdeck.mediaSubdeckId" class="flex items-center">
                <FileUpload
                  mode="advanced"
                  :auto="true"
                  choose-label="Replace current file"
                  :multiple="false"
                  class="w-full subdeck-file-upload ml-4"
                  :custom-upload="true"
                  :show-upload-button="false"
                  :show-cancel-button="false"
                  @select="(e) => handleSubdeckFileUpload(e, subdeck.id)"
                />
              </div>
            </template>
          </Card>
          </TransitionGroup>

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

        <!-- Submit Button -->
        <div class="mt-6 flex justify-center gap-2">
          <Button label="Update" class="p-button-lg p-button-success" @click="update(false)">
            <Icon name="material-symbols-light:refresh" size="1.5em" />
            Update
          </Button>

          <Button label="Update" class="p-button-lg p-button-success" @click="update(true)">
            <Icon name="material-symbols-light:refresh" size="1.5em" />
            Update & Reparse
          </Button>

          <Button label="Update" class="p-button-lg p-button-success" @click="fetchMetadata()">
            <Icon name="material-symbols-light:refresh" size="1.5em" />
            Fetch missing metadata
          </Button>

          <Button label="Update" class="p-button-lg p-button-success" @click="showRecomputeDifficultyDialog = true">
            <Icon name="material-symbols-light:calculate" size="1.5em" />
            Recompute Difficulty
          </Button>

          <Button label="Reaggregate" class="p-button-lg p-button-success" @click="showReaggregateDifficultyDialog = true">
            <Icon name="material-symbols-light:family-history" size="1.5em" />
            Reaggregate from Children
          </Button>
        </div>
      </div>
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
    border-color: #6366f1;
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
    font-size: 2rem;
    margin-bottom: 0.5rem;
  }

  .p-fileupload.p-fileupload-advanced .p-fileupload-content > div > span[data-pc-section='dndmessage'] {
    font-weight: bold;
  }

  .subdeck-card {
    transition: all 0.3s ease;
  }

  .subdeck-card:hover {
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
  }

  .subdeck-list-move {
    transition: transform 0.5s ease;
  }

  .subdeck-list-enter-active,
  .subdeck-list-leave-active {
    transition: all 0.5s ease;
  }

  .subdeck-list-enter-from,
  .subdeck-list-leave-to {
    opacity: 0;
    transform: translateX(30px);
  }

  .subdeck-list-leave-active {
    position: absolute;
  }
</style>
