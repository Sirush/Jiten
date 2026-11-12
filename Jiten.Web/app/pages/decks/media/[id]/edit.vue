<script setup lang="ts">
  import { debounce } from 'perfect-debounce';
  import { useToast } from 'primevue/usetoast';
  import type { Deck, MediaSuggestion, Tag } from '~/types';
  import { LinkType, MediaType } from '~/types/enums';
  import {
    MetadataField,
    type CharacterNameCheck,
    type EditableDeck,
    type EditablePendingEdit,
    type MetadataFranchisePick,
    type SubmitConflictField,
    type SubmitMetadataEditResult,
  } from '~/types/metadataEdits';
  import { dateToIso, isoToDate } from '~/utils/mediaListEntry';
  import { showErrorToast, showSuccessToast } from '~/utils/toast';
  import { coverUrl } from '~/utils/coverImage';
  import { getAllGenres } from '~/utils/genreMapper';
  import { getLinkTypeText, resolveLinkType } from '~/utils/linkTypeMapper';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { getEnumOptions } from '~/utils/enumUtils';
  import {
    fromRole,
    isLegacyGroupRelationship,
    relationshipPreviewText,
    relationshipRoleOptions,
    toCanonicalEdge,
    type RelationshipRoleOption,
  } from '~/utils/relationshipRoles';
  import { DEFAULT_TAG_PERCENTAGE, TAG_WEIGHT_LEVELS, clampTagPercentage, nearestTagWeight, searchTags, splitTagMatch } from '~/utils/tags';
  import { apiErrorMessage, apiStatusCode } from '~/utils/apiErrorMessage';
  import { showUndoToast } from '~/composables/useMediaListSync';
  import {
    MAX_ADDED_NAMES,
    MAX_ALIASES,
    MAX_DESCRIPTION_LENGTH,
    MAX_NOTE_LENGTH,
    MAX_SOURCES,
    NAME_LENGTH_ERROR,
    NOTE_REQUIRED_FIELDS,
    aliasError,
    buildSubmission,
    draftChanges,
    draftFromEditable,
    fieldLabel,
    isNameTooShort,
    joinList,
    linkUrlError,
    normaliseSourceUrl,
    rebaseDraft,
    relationshipLabel,
    relationshipTitles,
    resetDraftField,
    sourcesError,
    submissionError,
    submitOutcome,
    toggleChipClass,
    type MetadataDraft,
    type SubmitOutcome,
  } from '~/utils/metadataEdits';

  definePageMeta({
    middleware: ['auth'],
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const route = useRoute();
  const deckId = Number(route.params.id);
  const { $api } = useNuxtApp();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();

  const { data: editable, error: loadError, refresh } = useApiFetch<EditableDeck>(`media-deck/${deckId}/editable`, { server: false });
  const { data: tagVocabulary, status: tagsStatus } = useApiFetch<Tag[]>('media-deck/tags', { server: false, lazy: true });

  const original = ref<MetadataDraft | null>(null);
  const draft = ref<MetadataDraft | null>(null);
  const viewer = ref<EditableDeck['viewer'] | null>(null);
  const sources = ref<string[]>(['']);
  const note = ref('');
  const outcome = ref<SubmitOutcome | null>(null);
  const showRules = ref(false);
  const submitting = ref(false);
  const submitErrorText = ref<string | null>(null);
  const sourceErrorText = ref<string | null>(null);
  const pendingEdits = ref<EditablePendingEdit[]>([]);

  function initialise(deck: EditableDeck) {
    original.value = draftFromEditable(deck);
    draft.value = draftFromEditable(deck);
    viewer.value = { ...deck.viewer };
    pendingEdits.value = [...(deck.pending ?? [])];
    sources.value = [''];
    note.value = '';
    outcome.value = null;
    submitErrorText.value = null;
    sourceErrorText.value = null;
    nameChecks.value = {};
  }

  const withdrawingId = ref<number | null>(null);

  const fieldBindings = (field: MetadataField) => ({
    pending: pendingEdits.value.filter((p) => (p.fields & field) !== 0),
    deckId,
    withdrawingId: withdrawingId.value,
    changed: changedFields.value.has(field),
    onUndo: () => resetField(field),
    onWithdraw: withdraw,
  });

  async function withdraw(id: number) {
    if (withdrawingId.value) return;
    withdrawingId.value = id;
    try {
      await $api(`metadata-edits/${id}/withdraw`, { method: 'POST' });
      pendingEdits.value = pendingEdits.value.filter((p) => p.id !== id);
      showSuccessToast(toast, 'Suggestion withdrawn', 'It was taken out of the review queue.');
    } catch (err) {
      showErrorToast(toast, "The suggestion wasn't withdrawn", apiErrorMessage(err, 'Try again in a moment.'));
    } finally {
      withdrawingId.value = null;
    }
  }

  const releaseDateValue = computed<Date | null>({
    get: () => (draft.value?.releaseDate ? isoToDate(draft.value.releaseDate) : null),
    set: (value) => {
      if (draft.value) draft.value.releaseDate = value instanceof Date ? dateToIso(value) : '';
    },
  });
  const minReleaseDate = new Date(1800, 0, 1);
  const maxReleaseDate = new Date(new Date().getFullYear() + 5, 11, 31);

  const loadErrorStatus = computed(() => apiStatusCode(loadError.value));
  const parentDeckId = computed(() => (loadError.value as { data?: { parentDeckId?: number } } | null)?.data?.parentDeckId ?? null);

  const title = computed(() => (editable.value ? localiseTitle(editable.value) : ''));
  const breadcrumbDeck = computed(() => (editable.value ? (editable.value as unknown as Deck) : null));

  useHead(() => ({ title: title.value ? `Suggest an edit: ${title.value}` : 'Suggest an edit' }));
  useRobotsRule({ noindex: true, follow: false });

  const submission = computed(() => (original.value && draft.value ? buildSubmission(original.value, draft.value) : { payload: {}, fields: [] }));
  const changedFields = computed(() => new Set(submission.value.fields));
  const changes = computed(() => (original.value && draft.value ? draftChanges(original.value, draft.value, submission.value.fields) : []));

  const noteFields = computed(() => submission.value.fields.filter((f) => NOTE_REQUIRED_FIELDS.has(f)));
  const noteHint = computed(() =>
    noteFields.value.length ? `Required when changing ${joinList(noteFields.value.map((f) => fieldLabel(f).toLowerCase()))}` : 'Optional'
  );

  function resetField(field: MetadataField) {
    if (original.value && draft.value) resetDraftField(original.value, draft.value, field);
  }

  function discardChanges() {
    if (!draft.value || !editable.value) return;
    const previous = JSON.parse(JSON.stringify(draft.value)) as MetadataDraft;
    const previousSources = [...sources.value];
    const previousNote = note.value;
    draft.value = draftFromEditable(editable.value);
    sources.value = [''];
    note.value = '';
    submitErrorText.value = null;
    showUndoToast(toast, 'Changes discarded.', async () => {
      draft.value = previous;
      sources.value = previousSources;
      note.value = previousNote;
    });
  }

  const newAlias = ref('');
  const aliasErrorText = ref<string | null>(null);

  function addAlias() {
    if (!draft.value) return;
    const value = newAlias.value.trim();
    aliasErrorText.value = aliasError(value, draft.value.aliases);
    if (!value || aliasErrorText.value) return;
    draft.value.aliases.push(value);
    newAlias.value = '';
  }

  watch(newAlias, () => (aliasErrorText.value = null));

  const linkTypeOptions = getEnumOptions(LinkType, getLinkTypeText).sort((x, y) => x.label.localeCompare(y.label));

  const LINK_PLACEHOLDERS: Partial<Record<LinkType, string>> = {
    [LinkType.Vndb]: 'https://vndb.org/v...',
    [LinkType.Anilist]: 'https://anilist.co/...',
    [LinkType.Mal]: 'https://myanimelist.net/...',
    [LinkType.Tmdb]: 'https://www.themoviedb.org/...',
    [LinkType.Imdb]: 'https://www.imdb.com/title/...',
    [LinkType.Igdb]: 'https://www.igdb.com/games/...',
    [LinkType.Syosetsu]: 'https://ncode.syosetu.com/...',
    [LinkType.Kakuyomu]: 'https://kakuyomu.jp/works/...',
    [LinkType.Bookmeter]: 'https://bookmeter.com/books/...',
  };

  /** Switches the type only for a recognised site, so a half-typed URL never resets a deliberate choice to Website. */
  function detectLinkType(link: { linkType: LinkType | number }, value: string | undefined) {
    link.linkType = resolveLinkType(value, link.linkType as LinkType) ?? link.linkType;
  }

  function addLinkRow() {
    draft.value?.links.push({ linkType: LinkType.Web, url: '' });
    nextTick(() => document.getElementById(`link-url-${(draft.value?.links.length ?? 1) - 1}`)?.focus());
  }

  const genreOptions = getAllGenres();

  function toggleGenre(genre: number) {
    const d = draft.value;
    if (!d) return;
    const index = d.genres.indexOf(genre);
    if (index === -1) d.genres.push(genre);
    else d.genres.splice(index, 1);
  }

  const tagQuery = ref<string | Tag>('');
  const tagSuggestions = ref<Tag[]>([]);
  const tagQueryText = computed(() => (typeof tagQuery.value === 'string' ? tagQuery.value : ''));

  function completeTags(query: string) {
    const used = new Set(draft.value?.tags.map((t) => t.tagId) ?? []);
    tagSuggestions.value = searchTags(
      (tagVocabulary.value ?? []).filter((t) => !used.has(t.tagId)),
      query,
    );
  }

  function addTag(tag: Tag) {
    if (!draft.value || draft.value.tags.some((t) => t.tagId === tag.tagId)) return;
    draft.value.tags.push({ tagId: tag.tagId, name: tag.name, percentage: DEFAULT_TAG_PERCENTAGE });
    tagQuery.value = '';
  }

  function setTagPercentage(index: number, value: number | null) {
    const tag = draft.value?.tags[index];
    if (tag) tag.percentage = clampTagPercentage(value);
  }

  const relationRole = ref<RelationshipRoleOption | null>(null);
  const relationTargetId = ref<number | null>(null);
  const relationTarget = ref<MediaSuggestion | null>(null);
  const relationTargetTitle = computed(() => (relationTarget.value ? localiseTitle(relationTarget.value) : ''));
  const relationErrorText = ref<string | null>(null);

  function onRelationSelect(suggestion: MediaSuggestion | null) {
    relationTarget.value = suggestion;
  }

  const relationPreview = computed(() => {
    const role = relationRole.value;
    const target = relationTargetTitle.value;
    if (!role || !target) return '';
    return relationshipPreviewText(role, target, title.value);
  });

  function addRelationship() {
    const d = draft.value;
    relationErrorText.value = null;
    if (!d || !relationRole.value || !relationTargetId.value) return;
    if (relationTargetId.value === deckId) {
      relationErrorText.value = "A deck can't be related to itself.";
      return;
    }
    const edge = toCanonicalEdge(deckId, fromRole(relationTargetId.value, relationRole.value));
    const exists = d.relationships.some(
      (r) => r.sourceDeckId === edge.sourceDeckId && r.targetDeckId === edge.targetDeckId && r.relationshipType === edge.relationshipType
    );
    if (exists) {
      relationErrorText.value = 'That relationship is already on this deck.';
      return;
    }
    d.relationships.push({
      ...edge,
      otherDeckId: relationTargetId.value,
      otherDeckTitle: relationTarget.value?.originalTitle || `#${relationTargetId.value}`,
      otherDeckRomajiTitle: relationTarget.value?.romajiTitle,
      otherDeckEnglishTitle: relationTarget.value?.englishTitle,
    });
    relationRole.value = null;
    relationTargetId.value = null;
    relationTarget.value = null;
  }

  const isOriginalRelationship = (r: MetadataDraft['relationships'][number]) =>
    original.value?.relationships.some(
      (o) => o.sourceDeckId === r.sourceDeckId && o.targetDeckId === r.targetDeckId && o.relationshipType === r.relationshipType
    ) ?? false;

  const currentFranchise = computed(() => editable.value?.franchise ?? null);
  const franchisePickId = ref<number | null>(null);
  const franchiseLooking = ref(false);
  const franchiseErrorText = ref<string | null>(null);
  let franchiseRequest = 0;

  async function onFranchiseDeckSelect(suggestion: MediaSuggestion | null) {
    franchiseErrorText.value = null;
    if (!suggestion || !draft.value) return;
    if (suggestion.deckId === deckId) {
      franchiseErrorText.value = 'Pick another deck from the franchise.';
      return;
    }
    const request = ++franchiseRequest;
    franchiseLooking.value = true;
    try {
      const pick = await $api<MetadataFranchisePick>(`media-deck/${suggestion.deckId}/franchise-ref`);
      if (request !== franchiseRequest || !draft.value) return;
      draft.value.franchise = {
        franchiseId: pick.franchiseId,
        originalTitle: pick.originalTitle,
        romajiTitle: pick.romajiTitle,
        englishTitle: pick.englishTitle,
        withDeckId: pick.deckId,
      };
      franchisePickId.value = null;
    } catch (err) {
      if (request === franchiseRequest) franchiseErrorText.value = apiErrorMessage(err, "That deck's franchise didn't load. Try again.");
    } finally {
      if (request === franchiseRequest) franchiseLooking.value = false;
    }
  }

  const newName = ref('');
  const nameCheck = ref<CharacterNameCheck | null>(null);
  const nameChecking = ref(false);
  const nameCheckError = ref<string | null>(null);
  /** Check results for names already added to the draft, keyed by the name as typed. */
  const nameChecks = ref<Record<string, CharacterNameCheck>>({});
  let nameRequest = 0;

  const runNameCheck = debounce(async (value: string) => {
    const request = ++nameRequest;
    if (isNameTooShort(value)) {
      nameCheck.value = { input: value, error: NAME_LENGTH_ERROR, surfaces: [] };
      nameChecking.value = false;
      return;
    }
    try {
      const [result] = await $api<CharacterNameCheck[]>(`media-deck/${deckId}/character-names/check`, { method: 'POST', body: { names: [value] } });
      if (request !== nameRequest) return;
      nameCheck.value = result ?? null;
    } catch (err) {
      if (request !== nameRequest) return;
      nameCheck.value = null;
      nameCheckError.value = apiErrorMessage(err, "The name couldn't be checked. Try again in a moment.");
    } finally {
      if (request === nameRequest) nameChecking.value = false;
    }
  }, 400);

  watch(newName, (value) => {
    const trimmed = value.trim();
    nameCheck.value = null;
    nameCheckError.value = null;
    if (!trimmed) {
      nameRequest++;
      nameChecking.value = false;
      return;
    }
    nameChecking.value = true;
    runNameCheck(trimmed);
  });

  const keptSurfaces = (check: CharacterNameCheck | null | undefined) =>
    check?.error ? [] : (check?.surfaces ?? []).filter((s) => s.status === 'ok' || s.status === 'flagged').map((s) => s.surface);

  const canAddName = computed(
    () =>
      !!nameCheck.value &&
      nameCheck.value.input.trim() === newName.value.trim() &&
      keptSurfaces(nameCheck.value).length > 0 &&
      (draft.value?.addedNames.length ?? 0) < MAX_ADDED_NAMES &&
      !draft.value?.addedNames.some((n) => n.input === newName.value.trim())
  );

  function addName() {
    const d = draft.value;
    const check = nameCheck.value;
    if (!d || !check || !canAddName.value) return;
    const input = newName.value.trim();
    d.addedNames.push({ input, surfaces: keptSurfaces(check) });
    nameChecks.value = { ...nameChecks.value, [input]: check };
    newName.value = '';
  }

  function removeAddedName(index: number) {
    draft.value?.addedNames.splice(index, 1);
  }

  function removeName(surface: string) {
    const d = draft.value;
    if (!d) return;
    d.characterNames = d.characterNames.filter((n) => n !== surface);
  }

  const removedNames = computed(() => {
    const kept = new Set(draft.value?.characterNames ?? []);
    return (original.value?.characterNames ?? []).filter((n) => !kept.has(n));
  });

  function restoreName(surface: string) {
    draft.value?.characterNames.push(surface);
  }

  const mediaTypeOptions = getEnumOptions(MediaType, getMediaTypeText);

  function addSource() {
    if (sources.value.length >= MAX_SOURCES) return;
    sources.value.push('');
    nextTick(() => document.getElementById(`source-${sources.value.length - 1}`)?.focus());
  }

  /** The deck's own provider pages are the usual source, so they're one click away. */
  const suggestedSources = computed(() =>
    (editable.value?.links ?? []).filter((l) => l.url && !sources.value.some((s) => normaliseSourceUrl(s) === l.url)).slice(0, 3)
  );

  function useSource(url: string) {
    const empty = sources.value.findIndex((s) => !s.trim());
    if (empty >= 0) sources.value[empty] = url;
    else if (sources.value.length < MAX_SOURCES) sources.value.push(url);
    sourceErrorText.value = null;
  }

  function tidySource(index: number) {
    sources.value[index] = normaliseSourceUrl(sources.value[index] ?? '');
  }

  const addedSurfaceCount = computed(() => new Set(draft.value?.addedNames.flatMap((n) => n.surfaces) ?? []).size);

  const canSubmit = computed(
    () => !!viewer.value?.emailConfirmed && !!viewer.value?.hasDisplayName && !viewer.value?.banned && submission.value.fields.length > 0
  );

  async function submit() {
    if (!original.value || !draft.value || submitting.value) return;
    draft.value.description = draft.value.description.trim();
    sources.value = sources.value.map((s) => (s.trim() ? normaliseSourceUrl(s) : s));
    sourceErrorText.value = sourcesError(sources.value);
    if (sourceErrorText.value) {
      submitErrorText.value = null;
      return;
    }
    submitErrorText.value = submissionError({
      draft: draft.value,
      fields: submission.value.fields,
      sources: sources.value,
      note: note.value,
    });
    if (submitErrorText.value) return;

    submitting.value = true;
    try {
      const filledSources = sources.value.map((s) => s.trim()).filter(Boolean);
      const result = await $api<SubmitMetadataEditResult>(`media-deck/${deckId}/metadata-edits`, {
        method: 'POST',
        body: { ...submission.value.payload, sources: filledSources, note: note.value.trim() || undefined },
      });
      outcome.value = submitOutcome(result.appliedFields, result.pendingFields);
    } catch (err) {
      const message = apiErrorMessage(err, "Your edit couldn't be sent. Try again in a moment.");
      if (apiStatusCode(err) === 409 && original.value) {
        const changed = (err as { data?: { changedFields?: SubmitConflictField[] } }).data?.changedFields ?? [];
        for (const c of changed) rebaseDraft(original.value, c.field, c.current);
      }
      if (apiStatusCode(err) === 400 && /display name/i.test(message) && viewer.value) viewer.value.hasDisplayName = false;
      if (apiStatusCode(err) === 403 && viewer.value) viewer.value.banned = true;
      submitErrorText.value = message;
    } finally {
      submitting.value = false;
    }
  }

  async function startAnother() {
    await refresh();
    if (editable.value) initialise(editable.value);
  }

  const isDirty = computed(() => submission.value.fields.length > 0 && !outcome.value);
  const onBeforeUnload = (event: BeforeUnloadEvent) => {
    if (!isDirty.value) return;
    event.preventDefault();
  };
  onMounted(() => window.addEventListener('beforeunload', onBeforeUnload));
  onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload));

  /** Below lg the summary sits under a long form, so a bar keeps the pending changes and the way to submit them in reach. */
  const showMobileBar = computed(() => changes.value.length > 0 && !outcome.value);
  const MOBILE_BAR_OFFSET = '84px';

  function scrollToChanges() {
    document.getElementById('changes-heading')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  watch(showMobileBar, (visible) => {
    if (!import.meta.client) return;
    const narrow = window.matchMedia('(max-width: 1023px)').matches;
    document.documentElement.style.setProperty('--undo-toast-bottom', visible && narrow ? MOBILE_BAR_OFFSET : '');
  });
  onBeforeUnmount(() => document.documentElement.style.removeProperty('--undo-toast-bottom'));

  watch(
    editable,
    (deck) => {
      if (deck) initialise(deck);
    },
    { immediate: true }
  );

  const sectionClass = 'rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900';
  const sectionHeadClass = 'flex flex-wrap items-center justify-between gap-2 px-4 py-3 border-b border-surface-200 dark:border-surface-700';
  const chipClass =
    'inline-flex items-center gap-1.5 rounded-md border px-2 py-0.5 text-sm bg-surface-0 dark:bg-surface-900 border-surface-300 dark:border-surface-600';
  const chipRemoveClass =
    'inline-flex items-center justify-center w-5 h-5 rounded text-muted-color hover:text-color hover:bg-surface-100 dark:hover:bg-surface-800 cursor-pointer';
</script>

<template>
  <div class="flex flex-col gap-4" :class="showMobileBar ? 'pb-20 lg:pb-0' : ''">
    <DeckBreadcrumb :deck="breadcrumbDeck" current="Suggest an edit" />

    <div v-if="!editable && !loadError" class="grid grid-cols-1 lg:grid-cols-[minmax(0,1fr)_22rem] gap-5 items-start" aria-busy="true">
      <div class="flex flex-col gap-4">
        <Skeleton height="5.5rem" />
        <Skeleton height="22rem" />
        <Skeleton height="16rem" />
      </div>
      <Skeleton height="12rem" />
    </div>

    <section v-else-if="loadErrorStatus === 400 && parentDeckId" :class="[sectionClass, 'p-5 flex flex-col gap-3']">
      <h1 class="m-0 text-xl font-bold">Edit the main deck instead</h1>
      <p class="m-0 text-muted-color">{{ apiErrorMessage(loadError, 'This is a subdeck. Its details come from the main deck, so edits go there.') }}</p>
      <div>
        <Button as="router-link" :to="`/decks/media/${parentDeckId}/edit`" label="Suggest an edit on the main deck" />
      </div>
    </section>

    <section v-else-if="loadErrorStatus === 404" :class="[sectionClass, 'p-5 flex flex-col gap-3']">
      <h1 class="m-0 text-xl font-bold">Deck not found</h1>
      <p class="m-0 text-muted-color">This deck doesn't exist or was removed.</p>
      <div><Button as="router-link" to="/decks/media" label="Browse media" severity="secondary" /></div>
    </section>

    <section v-else-if="loadError || !editable || !draft || !viewer" :class="[sectionClass, 'p-5 flex flex-col items-start gap-3']">
      <p class="m-0">{{ apiErrorMessage(loadError, "The edit form didn't load.") }}</p>
      <Button label="Retry" icon="pi pi-refresh" @click="refresh()" />
    </section>

    <section v-else-if="!viewer.hasDisplayName" :class="[sectionClass, 'p-5 flex flex-col gap-3 max-w-xl']" aria-labelledby="display-name-heading">
      <h1 id="display-name-heading" class="m-0 text-xl font-bold">Choose a display name first</h1>
      <p class="m-0 text-sm text-muted-color">Edits are public. Your display name will be shown next to them in the deck history and the review queue.</p>
      <DisplayNameForm submit-label="Continue" submit-icon="pi pi-arrow-right" submit-icon-pos="right" stacked @saved="viewer.hasDisplayName = true" />
    </section>

    <template v-else>
      <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <h1 class="m-0 text-xl md:text-2xl font-bold">Suggest an edit</h1>
        <button type="button" class="text-sm text-primary-600 dark:text-primary-400 hover:underline cursor-pointer rounded" @click="showRules = true">
          Rules for suggesting edits
        </button>
      </div>
      <MetadataEditRules v-model:visible="showRules" />

      <Message v-if="!viewer.emailConfirmed" severity="warn" :closable="false">
        Confirm your email address before suggesting edits. Open the confirmation link sent to the address shown in your
        <NuxtLink to="/settings/account" class="underline">account settings</NuxtLink>, then reload this page.
      </Message>

      <div class="grid grid-cols-1 lg:grid-cols-[minmax(0,1fr)_22rem] gap-5 items-start">
        <fieldset :disabled="!!outcome" class="min-w-0 m-0 p-0 border-0 flex flex-col gap-4">
          <div :class="[sectionClass, 'flex items-center gap-3 px-4 py-3']">
            <img :src="coverUrl(editable.coverName, 'xs')" alt="" class="w-11 h-16 object-cover rounded shrink-0" />
            <div class="flex flex-col gap-0.5 min-w-0">
              <NuxtLink :to="`/decks/media/${deckId}/detail`" class="font-semibold truncate" v-bind="japaneseTextAttrs(title)">{{ title }}</NuxtLink>
              <span class="text-xs text-muted-color">
                {{ getMediaTypeText(editable.mediaType) }}<template v-if="editable.releaseYear">, {{ editable.releaseYear }}</template
                >, {{ editable.characterCount.toLocaleString() }} characters
              </span>
              <NuxtLink :to="`/decks/media/${deckId}/history`" class="text-xs">
                See edit history ({{ editable.historyCount.toLocaleString() }} {{ editable.historyCount === 1 ? 'edit' : 'edits' }})
              </NuxtLink>
            </div>
          </div>

          <section :class="sectionClass" aria-labelledby="titles-heading">
            <div :class="sectionHeadClass"><h2 id="titles-heading" class="m-0 text-base font-bold">Titles and release</h2></div>
            <div class="p-4 grid gap-4">
              <MetadataEditField label="Original title" input-id="f-original" v-bind="fieldBindings(MetadataField.OriginalTitle)">
                <InputText id="f-original" v-model="draft.originalTitle" class="w-full" v-bind="japaneseTextAttrs(draft.originalTitle)" />
              </MetadataEditField>
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <MetadataEditField label="Romaji title" input-id="f-romaji" v-bind="fieldBindings(MetadataField.RomajiTitle)">
                  <InputText id="f-romaji" v-model="draft.romajiTitle" class="w-full" />
                </MetadataEditField>
                <MetadataEditField label="English title" input-id="f-english" v-bind="fieldBindings(MetadataField.EnglishTitle)">
                  <InputText id="f-english" v-model="draft.englishTitle" class="w-full" />
                </MetadataEditField>
              </div>
              <p class="m-0 -mt-2 pl-3.5 text-xs text-muted-color">Use official English title only, or none if it doesn't exist.</p>

              <MetadataEditField label="Release date" input-id="f-date" class="max-w-72" v-bind="fieldBindings(MetadataField.ReleaseDate)">
                <DatePicker
                  v-model="releaseDateValue"
                  input-id="f-date"
                  date-format="yy-mm-dd"
                  placeholder="YYYY-MM-DD"
                  show-icon
                  show-button-bar
                  fluid
                  :min-date="minReleaseDate"
                  :max-date="maxReleaseDate"
                />
                <p class="m-0 mt-1 text-xs text-muted-color">The first Japanese release of this version.</p>
              </MetadataEditField>

              <MetadataEditField
                label="Aliases"
                input-id="f-alias"
                :count="`${draft.aliases.length} of ${MAX_ALIASES}`"
                v-bind="fieldBindings(MetadataField.Aliases)"
              >
                <div v-if="draft.aliases.length" class="flex flex-wrap gap-1.5 mb-2">
                  <span
                    v-for="(alias, index) in draft.aliases"
                    :key="alias"
                    :class="[chipClass, !original?.aliases.includes(alias) ? '!border-dashed !border-primary-500' : '']"
                  >
                    <span v-bind="japaneseTextAttrs(alias)">{{ alias }}</span>
                    <button type="button" :class="chipRemoveClass" :aria-label="`Remove ${alias}`" @click="draft.aliases.splice(index, 1)">
                      <i class="pi pi-times text-[10px]" aria-hidden="true" />
                    </button>
                  </span>
                </div>
                <div class="flex gap-2">
                  <InputText id="f-alias" v-model="newAlias" placeholder="Add an alias" class="w-full sm:max-w-72" @keydown.enter.prevent="addAlias" />
                  <Button label="Add" severity="secondary" :disabled="!newAlias.trim()" @click="addAlias" />
                </div>
                <p v-if="aliasErrorText" class="m-0 mt-1 text-xs text-red-600 dark:text-red-400" role="alert">{{ aliasErrorText }}</p>
                <p class="m-0 mt-1 text-xs text-muted-color">Aliases help search find this deck. Up to {{ MAX_ALIASES }}, official translated names or common nicknames/abbreviations.</p>
              </MetadataEditField>

              <MetadataEditField label="External links" v-bind="fieldBindings(MetadataField.Links)">
                <div class="flex flex-col gap-2">
                  <div v-for="(link, index) in draft.links" :key="index" class="flex flex-col gap-1">
                    <div class="grid grid-cols-[minmax(0,1fr)_2.75rem] sm:grid-cols-[10rem_minmax(0,1fr)_2.75rem] gap-2 items-center">
                      <Select
                        v-model="link.linkType"
                        :options="linkTypeOptions"
                        option-label="label"
                        option-value="value"
                        :aria-label="`Link ${index + 1} type`"
                        scroll-height="min(30rem, 60vh)"
                        class="col-span-2 sm:col-span-1 w-full"
                      />
                      <InputText
                        :id="`link-url-${index}`"
                        v-model="link.url"
                        type="url"
                        :placeholder="LINK_PLACEHOLDERS[link.linkType as LinkType] ?? 'https://...'"
                        :aria-label="`${getLinkTypeText(link.linkType)} link`"
                        :invalid="!!linkUrlError(link.linkType, link.url)"
                        class="w-full"
                        @update:model-value="(value) => detectLinkType(link, value)"
                      />
                      <Button
                        icon="pi pi-times"
                        severity="secondary"
                        text
                        :aria-label="`Remove ${getLinkTypeText(link.linkType)} link`"
                        class="!w-11 !h-11"
                        @click="draft.links.splice(index, 1)"
                      />
                    </div>
                    <p v-if="linkUrlError(link.linkType, link.url)" class="m-0 text-xs text-red-600 dark:text-red-400">
                      {{ linkUrlError(link.linkType, link.url) }}
                    </p>
                  </div>
                  <div><Button label="Add a link" icon="pi pi-plus" severity="secondary" text size="small" class="!px-0" @click="addLinkRow" /></div>
                </div>
                <p class="m-0 mt-1 text-xs text-muted-color">Link the page for this exact work, not a series or franchise page. Only add big databases or official sites.</p>
              </MetadataEditField>
            </div>
          </section>

          <section :class="sectionClass" aria-labelledby="genres-heading">
            <div :class="sectionHeadClass"><h2 id="genres-heading" class="m-0 text-base font-bold">Genres, tags and related decks</h2></div>
            <div class="p-4 grid gap-5">
              <MetadataEditField label="Genres" v-bind="fieldBindings(MetadataField.Genres)">
                <div class="flex flex-wrap gap-1.5" role="group" aria-label="Genres">
                  <button
                    v-for="genre in genreOptions"
                    :key="genre.value"
                    type="button"
                    :aria-pressed="draft.genres.includes(genre.value)"
                    :class="toggleChipClass(draft.genres.includes(genre.value))"
                    @click="toggleGenre(genre.value)"
                  >
                    {{ genre.label }}
                  </button>
                </div>
                <p class="m-0 mt-1 text-xs text-muted-color">Pick genres that describe most of the work, not a single scene.</p>
              </MetadataEditField>

              <MetadataEditField label="Tags" v-bind="fieldBindings(MetadataField.Tags)">
                <div class="flex flex-col gap-3 sm:gap-1.5">
                  <div
                    v-for="(tag, index) in draft.tags"
                    :key="tag.tagId"
                    class="grid grid-cols-[minmax(0,1fr)_2.75rem] sm:grid-cols-[minmax(0,1fr)_auto_2.75rem] gap-x-2 gap-y-1 items-center"
                  >
                    <span class="text-sm truncate" :title="tag.name">{{ tag.name }}</span>
                    <SelectButton
                      :model-value="nearestTagWeight(tag.percentage)"
                      :options="TAG_WEIGHT_LEVELS"
                      option-label="label"
                      option-value="value"
                      :allow-empty="false"
                      size="small"
                      :aria-label="`How central ${tag.name} is`"
                      class="col-span-2 order-last sm:col-span-1 sm:order-none"
                      @update:model-value="setTagPercentage(index, $event)"
                    />
                    <Button
                      icon="pi pi-times"
                      severity="secondary"
                      text
                      :aria-label="`Remove ${tag.name}`"
                      class="!w-11 !h-11"
                      @click="draft.tags.splice(index, 1)"
                    />
                  </div>
                  <p v-if="!draft.tags.length" class="m-0 text-sm text-muted-color">No tags yet.</p>
                </div>
                <AutoComplete
                  v-model="tagQuery"
                  :suggestions="tagSuggestions"
                  option-label="name"
                  placeholder="Type to add a tag"
                  dropdown
                  auto-option-focus
                  :loading="tagsStatus === 'pending'"
                  aria-label="Tag to add"
                  class="w-full sm:max-w-80 mt-2"
                  input-class="w-full"
                  @complete="(e: { query: string }) => completeTags(e.query)"
                  @option-select="(e: { value: Tag }) => addTag(e.value)"
                >
                  <template #option="{ option }">
                    <span class="text-sm">
                      {{ splitTagMatch(option.name, tagQueryText).before
                      }}<strong class="font-semibold">{{ splitTagMatch(option.name, tagQueryText).match }}</strong
                      >{{ splitTagMatch(option.name, tagQueryText).after }}
                    </span>
                  </template>
                  <template #empty>
                    <span class="text-sm text-muted-color">No tags match.</span>
                  </template>
                </AutoComplete>
                <p class="m-0 mt-1 text-xs text-muted-color">
                  NEVER add a spoiler tag that give away twists or the ending. Minor, moderate or major is how central the tag is to the work.
                </p>
              </MetadataEditField>

              <MetadataEditField label="Related decks" v-bind="fieldBindings(MetadataField.Relationships)">
                <div class="flex flex-col gap-1.5">
                  <div
                    v-for="(rel, index) in draft.relationships"
                    :key="`${rel.sourceDeckId}-${rel.targetDeckId}-${rel.relationshipType}`"
                    :class="[chipClass, 'justify-between w-full py-1', !isOriginalRelationship(rel) ? '!border-dashed !border-primary-500' : '']"
                  >
                    <span class="min-w-0 [overflow-wrap:anywhere]">
                      <NuxtLink :to="`/decks/media/${rel.otherDeckId}/detail`" class="text-color" v-bind="japaneseTextAttrs(localiseTitle(relationshipTitles(rel)))">
                        {{ relationshipLabel(rel, localiseTitle) }}
                      </NuxtLink>
                    </span>
                    <span v-if="isLegacyGroupRelationship(rel.relationshipType)" class="text-xs text-muted-color shrink-0">Kept until it becomes a series</span>
                    <button
                      v-else
                      type="button"
                      :class="chipRemoveClass"
                      :aria-label="`Remove ${relationshipLabel(rel, localiseTitle)}`"
                      @click="draft.relationships.splice(index, 1)"
                    >
                      <i class="pi pi-times text-[10px]" aria-hidden="true" />
                    </button>
                  </div>
                  <p v-if="!draft.relationships.length" class="m-0 text-sm text-muted-color">No related decks yet.</p>
                </div>
                <div class="grid grid-cols-1 sm:grid-cols-[13rem_minmax(0,1fr)_auto] gap-2 mt-2 items-start">
                  <Select
                    v-model="relationRole"
                    :options="relationshipRoleOptions"
                    option-label="label"
                    placeholder="Relationship type"
                    aria-label="Relationship type"
                    scroll-height="30rem"
                    class="w-full"
                  />
                  <MediaDeckPicker v-model="relationTargetId" :allow-raw-id="false" placeholder="Search media..." @select="onRelationSelect" />
                  <Button label="Add" severity="secondary" :disabled="!relationRole || !relationTargetId" @click="addRelationship" />
                </div>
                <p
                  v-if="relationPreview"
                  class="m-0 mt-2 text-sm rounded-md bg-surface-100 dark:bg-surface-800 px-3 py-2"
                  v-bind="japaneseTextAttrs(relationPreview)"
                >
                  {{ relationPreview }}
                </p>
                <p v-if="relationErrorText" class="m-0 mt-1 text-xs text-red-600 dark:text-red-400" role="alert">{{ relationErrorText }}</p>
                <p class="m-0 mt-1 text-xs text-muted-color">Only works in the same story: sequels, side stories, adaptations.</p>
              </MetadataEditField>

              <MetadataEditField label="Franchise" v-bind="fieldBindings(MetadataField.Franchise)">
                <p v-if="currentFranchise" class="m-0 text-sm">
                  In
                  <NuxtLink
                    :to="`/franchise/${currentFranchise.franchiseId}`"
                    class="font-medium"
                    v-bind="japaneseTextAttrs(localiseTitle(currentFranchise))"
                    >{{ localiseTitle(currentFranchise) }}</NuxtLink
                  >
                  <span class="text-muted-color"> ({{ currentFranchise.deckCount }} {{ currentFranchise.deckCount === 1 ? 'deck' : 'decks' }})</span>.
                  Related decks decide its franchise.
                </p>
                <template v-else>
                  <div v-if="draft.franchise" :class="[chipClass, 'justify-between w-full py-1 !border-dashed !border-primary-500']">
                    <span class="min-w-0 [overflow-wrap:anywhere]">
                      <template v-if="draft.franchise.franchiseId == null">New franchise with </template>
                      <NuxtLink
                        :to="draft.franchise.franchiseId != null ? `/franchise/${draft.franchise.franchiseId}` : `/decks/media/${draft.franchise.withDeckId}/detail`"
                        class="text-color"
                        v-bind="japaneseTextAttrs(localiseTitle(draft.franchise))"
                      >
                        {{ localiseTitle(draft.franchise) }}
                      </NuxtLink>
                    </span>
                    <button
                      type="button"
                      :class="chipRemoveClass"
                      :aria-label="`Remove ${localiseTitle(draft.franchise)}`"
                      @click="draft.franchise = null"
                    >
                      <i class="pi pi-times text-[10px]" aria-hidden="true" />
                    </button>
                  </div>
                  <div v-else class="flex items-center gap-2">
                    <MediaDeckPicker
                      v-model="franchisePickId"
                      :allow-raw-id="false"
                      placeholder="Search a deck from the franchise..."
                      class="w-full"
                      @select="onFranchiseDeckSelect"
                    />
                    <i v-if="franchiseLooking" class="pi pi-spin pi-spinner text-muted-color" aria-hidden="true" />
                  </div>
                  <p v-if="franchiseErrorText" class="m-0 mt-1 text-xs text-red-600 dark:text-red-400" role="alert">{{ franchiseErrorText }}</p>
                  <p class="m-0 mt-1 text-xs text-muted-color">
                    For works from the same franchise that are not directly related, or if there's a missing link in the database between them.
                  </p>
                </template>
              </MetadataEditField>
            </div>
          </section>

          <section :class="sectionClass" aria-labelledby="names-heading">
            <div :class="sectionHeadClass">
              <h2 id="names-heading" class="m-0 text-base font-bold">Character names</h2>
              <span class="text-xs text-muted-color tabular-nums">{{ (draft.characterNames.length + addedSurfaceCount).toLocaleString() }} names</span>
            </div>
            <div class="p-4 grid gap-4">
              <p class="m-0 text-sm text-muted-color">
                Names help Jiten split the text into words correctly. New names will only be reflected on the next reparse.
              </p>
              <MetadataEditField label="Names in this deck" v-bind="fieldBindings(MetadataField.CharacterNames)">
                <div class="flex flex-wrap gap-1.5">
                  <span v-for="name in draft.characterNames" :key="name" :class="chipClass">
                    <span lang="ja" class="ja-general">{{ name }}</span>
                    <button type="button" :class="chipRemoveClass" :aria-label="`Remove ${name}`" @click="removeName(name)">
                      <i class="pi pi-times text-[10px]" aria-hidden="true" />
                    </button>
                  </span>
                  <span v-for="(name, index) in draft.addedNames" :key="`added-${name.input}`" :class="[chipClass, '!border-dashed !border-primary-500']">
                    <span lang="ja" class="ja-general">{{ name.input }}</span>
                    <button type="button" :class="chipRemoveClass" :aria-label="`Remove ${name.input}`" @click="removeAddedName(index)">
                      <i class="pi pi-times text-[10px]" aria-hidden="true" />
                    </button>
                  </span>
                  <span v-if="!draft.characterNames.length && !draft.addedNames.length" class="text-sm text-muted-color">No names yet.</span>
                </div>
                <div v-if="removedNames.length" class="flex flex-wrap items-center gap-1.5 mt-2 text-sm">
                  <span class="text-xs text-muted-color">Will be removed:</span>
                  <button
                    v-for="name in removedNames"
                    :key="`removed-${name}`"
                    type="button"
                    class="rounded px-1 line-through text-muted-color hover:text-color cursor-pointer focus-visible:outline-2 focus-visible:outline-primary-500"
                    :aria-label="`Keep ${name}`"
                    @click="restoreName(name)"
                  >
                    <span lang="ja" class="ja-general">{{ name }}</span>
                  </button>
                </div>
              </MetadataEditField>

              <div class="pl-3">
                <label for="f-name" class="block text-xs font-semibold text-muted-color mb-1">Add a character</label>
                <div class="flex gap-2">
                  <InputText
                    id="f-name"
                    v-model="newName"
                    lang="ja"
                    placeholder="e.g. 前原 圭一"
                    class="w-full sm:max-w-80"
                    :disabled="draft.addedNames.length >= MAX_ADDED_NAMES"
                    @keydown.enter.prevent="addName"
                  />
                  <Button label="Add" severity="secondary" :disabled="!canAddName" @click="addName" />
                </div>
                <p class="m-0 mt-1 text-xs text-muted-color">
                  Write the full name as it appears in the text. Put a space between family and given name if you know where it splits. Do not include any
                  honorifics. Common nicknames can be added too.
                </p>
                <div class="mt-2 min-h-0" aria-live="polite">
                  <p v-if="nameChecking" class="m-0 text-xs text-muted-color">
                    <i class="pi pi-spin pi-spinner text-xs mr-1" aria-hidden="true" />Checking the text...
                  </p>
                  <p v-else-if="nameCheckError" class="m-0 text-xs text-red-600 dark:text-red-400">{{ nameCheckError }}</p>
                  <p v-else-if="nameCheck?.error" class="m-0 text-xs text-red-600 dark:text-red-400">{{ nameCheck.error }}</p>
                  <MetadataNameCheck v-else-if="nameCheck && nameCheck.surfaces.length" :surfaces="nameCheck.surfaces" />
                  <p v-if="draft.addedNames.length >= MAX_ADDED_NAMES" class="m-0 text-xs text-muted-color">
                    One edit can add up to {{ MAX_ADDED_NAMES }} names. Submit these first.
                  </p>
                </div>
                <div v-if="draft.addedNames.length" class="mt-3 flex flex-col gap-2">
                  <template v-for="name in draft.addedNames" :key="`check-${name.input}`">
                    <MetadataNameCheck
                      v-if="nameChecks[name.input]?.surfaces.length"
                      :surfaces="nameChecks[name.input]!.surfaces"
                      :title="`${name.input}: will add ${name.surfaces.length} ${name.surfaces.length === 1 ? 'entry' : 'entries'}`"
                    />
                  </template>
                </div>
              </div>
            </div>
          </section>

          <section :class="sectionClass" aria-labelledby="core-heading">
            <div :class="sectionHeadClass"><h2 id="core-heading" class="m-0 text-base font-bold">Media type and description</h2></div>
            <div class="p-4 grid gap-4">
              <MetadataEditField label="Media type" input-id="f-type" class="sm:max-w-80" v-bind="fieldBindings(MetadataField.MediaType)">
                <Select v-model="draft.mediaType" input-id="f-type" :options="mediaTypeOptions" option-label="label" option-value="value" class="w-full" />
              </MetadataEditField>
              <MetadataEditField
                label="Description"
                input-id="f-desc"
                :count="`${draft.description.length.toLocaleString()} / ${MAX_DESCRIPTION_LENGTH.toLocaleString()}`"
                v-bind="fieldBindings(MetadataField.Description)"
              >
                <Textarea
                  id="f-desc"
                  v-model="draft.description"
                  rows="5"
                  :maxlength="MAX_DESCRIPTION_LENGTH"
                  class="w-full resize-y"
                  v-bind="japaneseTextAttrs(draft.description)"
                  @blur="draft.description = draft.description.trim()"
                />
                <p class="m-0 mt-1 text-xs text-muted-color">A short, neutral summary with no spoilers. Do not use machine translation or AI.</p>
              </MetadataEditField>
              <p class="m-0 text-xs text-muted-color pl-3">
                Cover images and anything computed from the text can't be edited here. For a wrong or broken file, use Report an issue on the deck page.
              </p>
            </div>
          </section>
        </fieldset>

        <aside class="lg:sticky lg:top-4" aria-labelledby="changes-heading">
          <div :class="[sectionClass, 'p-4 flex flex-col gap-3']">
            <div class="flex items-center justify-between gap-2">
              <h2 id="changes-heading" class="m-0 text-base font-bold">Your changes</h2>
              <span v-if="changes.length && !outcome" class="text-xs text-muted-color tabular-nums">
                {{ changes.length }} {{ changes.length === 1 ? 'field' : 'fields' }}
              </span>
            </div>

            <div v-if="outcome" class="flex flex-col gap-3" role="status">
              <div
                class="rounded-md px-3 py-2.5 flex flex-col gap-1.5"
                :class="outcome.kind === 'pending' ? 'bg-amber-50 dark:bg-amber-900/20' : 'bg-green-50 dark:bg-green-900/20'"
              >
                <Tag
                  :value="outcome.kind === 'live' ? 'Live' : outcome.kind === 'pending' ? 'Waiting for review' : 'Partly live'"
                  :severity="outcome.kind === 'pending' ? 'warn' : 'success'"
                  class="self-start"
                />
                <strong class="text-sm">{{ outcome.title }}</strong>
                <p class="m-0 text-sm text-muted-color">{{ outcome.body }}</p>
              </div>
              <div class="flex flex-wrap gap-2">
                <Button as="router-link" :to="`/decks/media/${deckId}/history`" label="View deck history" severity="secondary" />
                <Button label="Start another edit" text @click="startAnother" />
              </div>
            </div>

            <p v-else-if="!changes.length" class="m-0 text-sm text-muted-color">Nothing changed yet. Edit any field and it will show up here.</p>

            <template v-else>
              <MetadataChangeList :changes="changes" />

              <p v-if="viewer.banned" class="m-0 pt-2 border-t border-surface-200 dark:border-surface-700 text-sm" role="status">
                Your account can't suggest edits right now.
              </p>
              <template v-else>
                <div class="flex flex-col gap-1.5 pt-2 border-t border-surface-200 dark:border-surface-700">
                  <div class="flex items-center gap-2">
                    <label for="source-0" class="text-xs font-semibold text-muted-color">Sources <span class="font-normal">(optional)</span></label>
                    <span class="text-xs text-muted-color tabular-nums">{{ sources.length }} of {{ MAX_SOURCES }}</span>
                  </div>
                  <div v-for="(_, index) in sources" :key="index" class="grid grid-cols-[minmax(0,1fr)_2.75rem] gap-1 items-center">
                    <InputText
                      :id="`source-${index}`"
                      v-model="sources[index]"
                      type="url"
                      placeholder="https://..."
                      :aria-label="`Source ${index + 1}`"
                      :invalid="!!sourceErrorText"
                      :aria-describedby="sourceErrorText ? 'source-error' : undefined"
                      class="w-full"
                      @update:model-value="sourceErrorText = null"
                      @blur="tidySource(index)"
                    />
                    <Button
                      v-if="sources.length > 1"
                      icon="pi pi-times"
                      severity="secondary"
                      text
                      :aria-label="`Remove source ${index + 1}`"
                      class="!w-11 !h-11"
                      @click="sources.splice(index, 1)"
                    />
                  </div>
                  <p v-if="sourceErrorText" id="source-error" class="m-0 text-xs text-red-600 dark:text-red-400" role="alert">{{ sourceErrorText }}</p>
                  <div v-if="suggestedSources.length" class="flex flex-wrap items-center gap-1.5">
                    <span class="text-xs text-muted-color">Use</span>
                    <button
                      v-for="link in suggestedSources"
                      :key="link.url"
                      type="button"
                      :class="[chipClass, 'cursor-pointer hover:bg-surface-100 dark:hover:bg-surface-800 !text-xs']"
                      :title="link.url"
                      @click="useSource(link.url)"
                    >
                      <i class="pi pi-plus text-[0.6rem]" aria-hidden="true" />{{ getLinkTypeText(link.linkType) }} page
                    </button>
                  </div>
                  <div v-if="sources.length < MAX_SOURCES">
                    <Button label="Add another source" icon="pi pi-plus" severity="secondary" text size="small" class="!px-0" @click="addSource" />
                  </div>
                  <p class="m-0 text-xs text-muted-color">Links where someone can check these changes, such as Wikipedia or a publisher page.</p>
                </div>

                <div class="flex flex-col gap-1.5">
                  <div class="flex items-center gap-2">
                    <label for="f-note" class="text-xs font-semibold text-muted-color">Note</label>
                    <span class="text-xs" :class="noteFields.length ? 'text-red-600 dark:text-red-400' : 'text-muted-color'">{{ noteHint }}</span>
                  </div>
                  <Textarea
                    id="f-note"
                    v-model="note"
                    rows="2"
                    :maxlength="MAX_NOTE_LENGTH"
                    placeholder="Anything that helps the editors check the data"
                    class="w-full resize-y"
                  />
                </div>

                <p v-if="submitErrorText" class="m-0 text-sm text-red-600 dark:text-red-400" role="alert">{{ submitErrorText }}</p>

                <p class="m-0 text-xs text-muted-color">
                  By clicking Submit, you agree that you've read the
                  <button type="button" class="text-primary-600 dark:text-primary-400 hover:underline cursor-pointer rounded" @click="showRules = true">
                    rules</button
                  >.
                </p>
                <div class="flex flex-wrap items-center gap-2">
                  <Button label="Submit" :loading="submitting" :disabled="!canSubmit" @click="submit" />
                  <Button label="Discard changes" severity="secondary" text :disabled="submitting" @click="discardChanges" />
                </div>
              </template>
            </template>
          </div>
        </aside>
      </div>

      <div
        v-if="showMobileBar"
        class="lg:hidden fixed inset-x-0 bottom-0 z-40 flex items-center justify-between gap-3 px-4 py-3 border-t border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-[0_-2px_8px_rgba(0,0,0,0.08)]"
      >
        <span class="text-sm tabular-nums">{{ changes.length }} {{ changes.length === 1 ? 'field' : 'fields' }} changed</span>
        <Button label="Review and submit" size="small" @click="scrollToChanges" />
      </div>
    </template>
  </div>
</template>
