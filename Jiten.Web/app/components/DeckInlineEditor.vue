<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import type { Deck, DeckMetadataPatchResult, MediaSuggestion, Tag } from '~/types';
  import { LinkType } from '~/types';
  import { getAllGenres, getGenreText } from '~/utils/genreMapper';
  import { getLinkTypeText } from '~/utils/linkTypeMapper';
  import { getRelationshipRoleLabel, relationshipRoleOptions, type RelationshipRoleOption } from '~/utils/relationshipRoles';
  import { DEFAULT_TAG_PERCENTAGE, NOT_ORIGINALLY_JP_FALLBACK_NAME, NOT_ORIGINALLY_JP_TAG_ID } from '~/utils/tags';

  const props = defineProps<{ deck: Deck }>();

  const emit = defineEmits<{
    saved: [result: DeckMetadataPatchResult];
    close: [];
  }>();

  const toast = useToast();
  const confirm = useConfirm();
  const localiseTitle = useLocaliseTitle();

  const {
    draft,
    isDirty,
    saving,
    save: saveMetadata,
    tagVocabulary,
    tagsLoading,
    loadTagVocabulary,
    hasGenre,
    toggleGenre,
    hasTag,
    toggleTag,
    addTagById,
    removeTag,
    setTagPercentage,
    addLink,
    removeLink,
    addRelationship,
    removeRelationship,
  } = useDeckInlineEdit(() => props.deck);

  const genrePopover = ref();
  const tagPopover = ref();
  // Relations and links are dialogs, not popovers: a Popover hides on any ancestor scroll, and
  // opening a nested Select near the viewport bottom scrolls the focused option into view.
  const showRelationDialog = ref(false);
  const showLinkDialog = ref(false);

  const genreOptions = getAllGenres();

  const tagSearchQuery = ref('');
  const filteredTags = computed(() => {
    const query = tagSearchQuery.value.trim().toLowerCase();
    if (!query) return tagVocabulary.value;
    return tagVocabulary.value.filter((t) => t.name.toLowerCase().includes(query));
  });

  const relationTargetId = ref<number | null>(null);
  const relationTargetTitle = ref('');
  const relationRole = ref<RelationshipRoleOption | null>(null);

  const linkType = ref<LinkType>(LinkType.Web);
  const linkUrl = ref('');
  // Non-null while the popover is editing an existing link rather than adding one.
  const linkEditIndex = ref<number | null>(null);

  const linkTypeOptions = Object.values(LinkType)
    .filter((value): value is LinkType => typeof value === 'number')
    .map((value) => ({ value, label: getLinkTypeText(value) }));

  const relationshipPreview = computed(() => {
    const role = relationRole.value;
    const target = relationTargetTitle.value || (relationTargetId.value ? `#${relationTargetId.value}` : '');
    if (!role || !target) return '';
    const thisTitle = draft.value.originalTitle || 'this deck';
    if (role.label === 'Alternative') return `${target} will be an alternative version of ${thisTitle}.`;
    if (role.label === 'Same series') return `${target} will be in the same series as ${thisTitle}.`;
    if (role.label === 'Same setting') return `${target} will share its setting with ${thisTitle}.`;
    return `${target} will be the ${role.label.toLowerCase()} of ${thisTitle}.`;
  });

  const warn = (detail: string) => toast.add({ severity: 'warn', summary: 'Validation', detail, life: 3000 });

  const openGenres = (event: Event) => genrePopover.value?.toggle(event);

  const openTags = async (event: Event) => {
    tagSearchQuery.value = '';
    tagPopover.value?.toggle(event);
    await loadTagVocabulary();
  };

  const openRelations = () => {
    relationTargetId.value = null;
    relationTargetTitle.value = '';
    relationRole.value = null;
    showRelationDialog.value = true;
  };

  const openLinkAdd = () => {
    linkEditIndex.value = null;
    linkType.value = LinkType.Web;
    linkUrl.value = '';
    showLinkDialog.value = true;
  };

  const openLinkEdit = (index: number) => {
    const link = draft.value.links[index];
    if (!link) return;
    linkEditIndex.value = index;
    linkType.value = link.linkType;
    linkUrl.value = link.url;
    showLinkDialog.value = true;
  };

  function onRelationSelect(suggestion: MediaSuggestion | null) {
    relationTargetTitle.value = suggestion ? localiseTitle(suggestion) : '';
  }

  function commitRelation() {
    if (!relationTargetId.value || !relationRole.value) {
      warn('Pick a deck and a relationship type');
      return;
    }
    const error = addRelationship(relationTargetId.value, relationTargetTitle.value || `#${relationTargetId.value}`, relationRole.value);
    if (error) {
      warn(error);
      return;
    }
    showRelationDialog.value = false;
  }

  function commitLink() {
    const error = addLink(linkType.value, linkUrl.value, linkEditIndex.value);
    if (error) {
      warn(error);
      return;
    }
    linkEditIndex.value = null;
    showLinkDialog.value = false;
  }

  function commitTopTagMatch() {
    const top = filteredTags.value[0];
    if (!top) return;
    toggleTag(top);
    tagSearchQuery.value = '';
  }

  function onTagRowToggle(tag: Tag) {
    toggleTag(tag);
  }

  async function save() {
    if (!draft.value.originalTitle.trim()) {
      warn('Original title is required');
      return;
    }

    if (!isDirty.value) {
      emit('close');
      return;
    }

    try {
      const result = await saveMetadata();
      toast.add({ severity: 'success', summary: 'Saved', detail: 'Deck metadata updated', life: 2500 });
      emit('saved', result);
    } catch (error) {
      console.error('Inline deck metadata save failed', error);
      const apiMessage = (error as { data?: { message?: string } })?.data?.message;
      toast.add({
        severity: 'error',
        summary: 'Save failed',
        detail: apiMessage ?? (error instanceof Error ? error.message : 'The metadata could not be saved'),
        life: 5000,
      });
    }
  }

  function requestClose() {
    if (!isDirty.value) {
      emit('close');
      return;
    }
    confirm.require({
      message: 'Discard your unsaved metadata changes?',
      header: 'Discard changes',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Discard',
      rejectLabel: 'Keep editing',
      acceptProps: { severity: 'danger' },
      accept: () => emit('close'),
    });
  }

  const onKeydown = (event: KeyboardEvent) => {
    if (event.key !== 'Escape') return;
    // An open popover or confirm dialog owns Escape first; leaving edit mode underneath it is never intended.
    if (document.querySelector('.p-popover, .p-dialog')) return;
    requestClose();
  };

  onMounted(() => window.addEventListener('keydown', onKeydown));
  onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown));
</script>

<template>
  <div class="mt-4 rounded-lg border border-primary-400/60 dark:border-primary-500/40 bg-surface-50/60 dark:bg-surface-900/30 p-3 space-y-3">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <span class="text-xs font-semibold uppercase tracking-wider text-primary-600 dark:text-primary-400">
        Editing metadata
        <span v-if="isDirty" class="ml-1 inline-block h-2 w-2 rounded-full bg-amber-500 align-middle" aria-label="Unsaved changes" />
      </span>
      <div class="flex gap-2">
        <Button label="Cancel" severity="secondary" size="small" :disabled="saving" @click="requestClose" />
        <Button label="Save" icon="pi pi-check" size="small" :loading="saving" @click="save" />
      </div>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-3 gap-2">
      <div class="flex flex-col gap-1">
        <label class="text-xs text-gray-500 dark:text-gray-400" for="inline-original-title">Original title</label>
        <InputText id="inline-original-title" v-model="draft.originalTitle" size="small" lang="ja" />
      </div>
      <div class="flex flex-col gap-1">
        <label class="text-xs text-gray-500 dark:text-gray-400" for="inline-romaji-title">Romaji title</label>
        <InputText id="inline-romaji-title" v-model="draft.romajiTitle" size="small" />
      </div>
      <div class="flex flex-col gap-1">
        <label class="text-xs text-gray-500 dark:text-gray-400" for="inline-english-title">English title</label>
        <InputText id="inline-english-title" v-model="draft.englishTitle" size="small" />
      </div>
    </div>

    <div class="flex flex-col gap-1">
      <label class="text-xs text-gray-500 dark:text-gray-400" for="inline-description">Description</label>
      <Textarea id="inline-description" v-model="draft.description" rows="2" class="w-full resize-y" />
    </div>

    <div class="flex flex-wrap gap-x-6 gap-y-2">
      <div class="flex items-center gap-2">
        <Checkbox v-model="draft.hideDialoguePercentage" input-id="inline-hide-dialogue" binary />
        <label class="text-sm" for="inline-hide-dialogue">Hide dialogue percentage</label>
      </div>
      <div class="flex items-center gap-2">
        <Checkbox v-model="draft.hideAverageSentenceLength" input-id="inline-hide-asl" binary />
        <label class="text-sm" for="inline-hide-asl">Hide average sentence length</label>
      </div>
    </div>

    <div class="flex flex-wrap gap-1.5 items-center">
      <span class="text-xs font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mr-1 shrink-0 w-20">Genres</span>
      <button
        v-for="genre in draft.genres"
        :key="genre"
        type="button"
        class="inline-flex items-center rounded-full text-xs py-0.5 px-2 transition-colors cursor-pointer bg-purple-100 dark:bg-purple-900/50 text-purple-700 dark:text-purple-200 hover:bg-purple-200 dark:hover:bg-purple-800/60"
        @click="toggleGenre(genre)"
      >
        {{ getGenreText(genre) }}
        <i class="pi pi-times text-[10px] ml-1 opacity-70" />
      </button>
      <Button label="Genre" icon="pi pi-plus" size="small" severity="secondary" text @click="openGenres" />
    </div>

    <div class="flex flex-wrap gap-1.5 items-center">
      <span class="text-xs font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mr-1 shrink-0 w-20">Tags</span>
      <button
        v-for="tag in draft.tags"
        :key="tag.tagId"
        type="button"
        class="inline-flex items-center rounded-full text-xs py-0.5 px-2 transition-colors cursor-pointer bg-blue-100 dark:bg-blue-900/50 text-blue-700 dark:text-blue-200 hover:bg-blue-200 dark:hover:bg-blue-800/60"
        @click="removeTag(tag.tagId)"
      >
        {{ tag.name ?? `#${tag.tagId}` }}
        <span v-if="tag.percentage !== DEFAULT_TAG_PERCENTAGE" class="ml-1 opacity-70">{{ tag.percentage }}%</span>
        <i class="pi pi-times text-[10px] ml-1 opacity-70" />
      </button>
      <Button label="Tag" icon="pi pi-plus" size="small" severity="secondary" text @click="openTags" />
      <Button
        v-if="!hasTag(NOT_ORIGINALLY_JP_TAG_ID)"
        label="Not originally JP"
        icon="pi pi-plus"
        size="small"
        severity="secondary"
        text
        @click="addTagById(NOT_ORIGINALLY_JP_TAG_ID, NOT_ORIGINALLY_JP_FALLBACK_NAME)"
      />
    </div>

    <div class="flex flex-wrap gap-1.5 items-center">
      <span class="text-xs font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mr-1 shrink-0 w-20">Related</span>
      <button
        v-for="(rel, index) in draft.relationships"
        :key="`${rel.targetDeckId}-${rel.relationshipType}`"
        type="button"
        class="inline-flex items-center rounded-full text-xs py-0.5 px-2 transition-colors cursor-pointer bg-surface-100 dark:bg-surface-900/50 text-surface-700 dark:text-surface-200 hover:bg-surface-200 dark:hover:bg-surface-800/60"
        @click="removeRelationship(index)"
      >
        <span class="font-medium">{{ getRelationshipRoleLabel(rel.relationshipType) }}:</span>
        <span class="ml-1">{{ rel.targetTitle }}</span>
        <i class="pi pi-times text-[10px] ml-1 opacity-70" />
      </button>
      <Button label="Relation" icon="pi pi-plus" size="small" severity="secondary" text @click="openRelations" />
    </div>

    <div class="flex flex-wrap gap-1.5 items-center">
      <span class="text-xs font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mr-1 shrink-0 w-20">Links</span>
      <span v-for="(link, index) in draft.links" :key="`${link.linkType}-${link.url}`" class="inline-flex items-center rounded-full text-xs py-0.5 px-2 transition-colors bg-surface-100 dark:bg-surface-900/50 text-surface-700 dark:text-surface-200">
        <span :title="link.url">{{ getLinkTypeText(link.linkType) }}</span>
        <button type="button" class="ml-1 opacity-70 hover:opacity-100 cursor-pointer" aria-label="Edit link" @click="openLinkEdit(index)">
          <i class="pi pi-pencil text-[10px]" />
        </button>
        <button type="button" class="ml-1 opacity-70 hover:opacity-100 cursor-pointer" aria-label="Remove link" @click="removeLink(index)">
          <i class="pi pi-times text-[10px]" />
        </button>
      </span>
      <Button label="Link" icon="pi pi-plus" size="small" severity="secondary" text @click="openLinkAdd" />
    </div>

    <Popover ref="genrePopover" class="w-[min(26rem,calc(100vw_-_2rem))]">
      <div class="flex flex-wrap gap-1.5 p-1">
        <button
          v-for="genre in genreOptions"
          :key="genre.value"
          type="button"
          class="inline-flex items-center rounded-full text-xs py-0.5 px-2 transition-colors cursor-pointer"
          :class="hasGenre(genre.value)
            ? 'bg-purple-100 dark:bg-purple-900/50 text-purple-700 dark:text-purple-200'
            : 'bg-surface-100 dark:bg-surface-900/50 text-surface-600 dark:text-surface-300 hover:bg-surface-200 dark:hover:bg-surface-800/60'"
          @click="toggleGenre(genre.value)"
        >
          {{ genre.label }}
        </button>
      </div>
    </Popover>

    <Popover ref="tagPopover" class="w-[min(26rem,calc(100vw_-_2rem))]">
      <div class="flex flex-col gap-2 p-1">
        <InputText
          v-model="tagSearchQuery"
          placeholder="Search tags..."
          size="small"
          autofocus
          @keydown.enter.prevent="commitTopTagMatch"
        />
        <div v-if="tagsLoading" class="text-sm text-muted-color py-2">Loading tags...</div>
        <div v-else class="max-h-[50vh] overflow-y-auto flex flex-col gap-1">
          <div v-if="!filteredTags.length" class="text-sm text-muted-color py-2">No tags match.</div>
          <div
            v-for="tag in filteredTags"
            :key="tag.tagId"
            class="flex items-center gap-2 rounded px-2 py-1 hover:bg-surface-100 dark:hover:bg-surface-800"
          >
            <button type="button" class="flex-1 text-left text-sm cursor-pointer" @click="onTagRowToggle(tag)">
              <i :class="['pi mr-2 text-xs', hasTag(tag.tagId) ? 'pi-check-square text-primary-500' : 'pi-stop text-gray-400']" />
              {{ tag.name }}
            </button>
            <InputNumber
              v-if="hasTag(tag.tagId)"
              :model-value="draft.tags.find((t) => t.tagId === tag.tagId)?.percentage ?? DEFAULT_TAG_PERCENTAGE"
              :min="0"
              :max="100"
              suffix="%"
              size="small"
              input-class="w-14 text-xs"
              @update:model-value="(value) => setTagPercentage(tag.tagId, Number(value ?? 0))"
            />
          </div>
        </div>
      </div>
    </Popover>

    <Dialog v-model:visible="showRelationDialog" modal header="Add relationship" class="w-full" style="max-width: 32rem">
      <div class="flex flex-col gap-3">
        <MediaDeckPicker v-model="relationTargetId" show-recent placeholder="Search media..." @select="onRelationSelect" />
        <Select
          v-model="relationRole"
          :options="relationshipRoleOptions"
          option-label="label"
          placeholder="Relationship type"
          class="w-full"
        />
        <p v-if="relationshipPreview" class="text-sm rounded bg-surface-100 dark:bg-surface-800 p-2 border-l-4 border-primary m-0">
          {{ relationshipPreview }}
        </p>
      </div>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="showRelationDialog = false" />
        <Button label="Add relationship" :disabled="!relationTargetId || !relationRole" @click="commitRelation" />
      </template>
    </Dialog>

    <Dialog
      v-model:visible="showLinkDialog"
      modal
      :header="linkEditIndex === null ? 'Add link' : 'Edit link'"
      class="w-full"
      style="max-width: 32rem"
    >
      <div class="flex flex-col gap-3">
        <Select v-model="linkType" :options="linkTypeOptions" option-label="label" option-value="value" class="w-full" />
        <InputText v-model="linkUrl" placeholder="https://..." autofocus @keydown.enter.prevent="commitLink" />
      </div>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="showLinkDialog = false" />
        <Button :label="linkEditIndex === null ? 'Add link' : 'Save link'" @click="commitLink" />
      </template>
    </Dialog>
  </div>
</template>
