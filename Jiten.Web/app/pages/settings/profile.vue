<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { useDebounceFn } from '@vueuse/core';
  import type {
    AudienceLevel,
    DictionaryEntry,
    ProfileFavouriteWord,
    ProfileFieldKey,
    ProfileHeader,
    ProfilePrivacy,
    ProfileSectionKey,
    ProfileVisibility,
  } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import {
    AVATAR_STYLES,
    PREMADE_AVATARS,
    PROFILE_COLOURS,
    PROFILE_PALETTE,
    avatarCharacter,
    isAvatarCharacter,
    headerBackground,
    ownProfileHandle,
    profilePath,
    type AvatarStyle,
    type ProfileColour,
  } from '~/utils/profileAppearance';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Profile - Settings' });

  const BIO_LIMIT = 200;
  const MAX_LINKS = 3;
  const MAX_WORDS = 5;
  const SELECTED_RING = 'ring-2 ring-primary-500 ring-offset-2 ring-offset-surface-0 dark:ring-offset-surface-900';

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const auth = useAuthStore();
  const localiseTitle = useLocaliseTitle();
  const pins = useProfilePins();

  const headerFetch = useApiFetch<ProfileHeader>('user/profile');
  const wordsFetch = useApiFetch<ProfileFavouriteWord[]>('user/profile/favourite-words');
  const { data: pinnedData, refresh: refreshPinned } = useAsyncData('settings-profile-pins', () => pins.load(), { lazy: true });
  await Promise.all([headerFetch.ready, wordsFetch.ready]);
  const { data: header, refresh: refreshHeader } = headerFetch;
  const { data: wordsData } = wordsFetch;
  const handle = computed(() => header.value?.displayName ?? ownProfileHandle(auth.user));

  const bio = ref('');
  const links = ref<string[]>(['']);
  const avatarKanji = ref('');
  const avatarColour = ref<ProfileColour | null>(null);
  const avatarStyle = ref<AvatarStyle>('filled');
  const headerColour = ref<ProfileColour | null>(null);
  const privacy = ref<ProfilePrivacy | null>(null);

  function resetForm() {
    const h = header.value;
    if (!h) return;
    bio.value = h.bio ?? '';
    links.value = h.links.length ? [...h.links] : [''];
    avatarKanji.value = h.avatarKanji ?? '';
    avatarColour.value = (h.avatarColour as ProfileColour | null) ?? null;
    avatarStyle.value = h.avatarStyle;
    headerColour.value = (h.headerColour as ProfileColour | null) ?? null;
    privacy.value = h.privacy ? JSON.parse(JSON.stringify(h.privacy)) : null;
    savedSnapshot.value = formSnapshot();
  }

  function formPayload() {
    return {
      bio: bio.value.trim(),
      links: links.value.map((l) => l.trim()).filter(Boolean),
      avatarKanji: avatarKanji.value.trim(),
      avatarColour: avatarColour.value ?? '',
      avatarStyle: avatarStyle.value,
      headerColour: headerColour.value ?? '',
      privacy: privacy.value && { ...privacy.value, hiddenFields: [...privacy.value.hiddenFields].sort() },
    };
  }

  const savedSnapshot = ref('');
  const formSnapshot = () => JSON.stringify(formPayload());
  const isDirty = computed(() => formSnapshot() !== savedSnapshot.value);
  watch(header, resetForm, { immediate: true });

  const name = computed(() => header.value?.displayName ?? auth.user?.userName ?? '');
  const bioTooLong = computed(() => bio.value.trim().length > BIO_LIMIT);

  const avatarKanjiValid = computed(() => {
    const value = avatarKanji.value.trim();
    return value.length === 0 || isAvatarCharacter(value);
  });
  /** Lags the check while typing so a half-composed IME entry isn't flagged; a fix clears it at once. */
  const showAvatarKanjiError = ref(false);
  const flagAvatarKanji = useDebounceFn(() => {
    showAvatarKanjiError.value = !avatarKanjiValid.value;
  }, 400);
  watch(avatarKanjiValid, (valid) => {
    if (valid) showAvatarKanjiError.value = false;
    else void flagAvatarKanji();
  });

  const visibilityOptions: { label: string; value: ProfileVisibility; description: string }[] = [
    { label: 'Public', value: 'public', description: 'Anyone with the link can open your profile.' },
    { label: 'Private', value: 'private', description: 'Only you can see your profile.' },
  ];

  const audienceOptions: { label: string; value: AudienceLevel }[] = [
    { label: 'Everyone', value: 'everyone' },
    { label: 'Only me', value: 'onlyMe' },
  ];

  const sectionRows: { key: ProfileSectionKey; label: string; description: string }[] = [
    { key: 'library', label: 'Library', description: 'Your titles, what you are reading now and your genres.' },
    { key: 'scores', label: 'Scores', description: 'The scores you give titles.' },
    { key: 'pinned', label: 'Pinned titles and favourite words', description: 'The rows you pin below.' },
    { key: 'vocabulary', label: 'Vocabulary and kanji', description: 'Words and kanji known, and vocabulary from finished titles.' },
    { key: 'studyActivity', label: 'Study activity', description: 'Your review heatmap.' },
    { key: 'stats', label: 'Stats', description: 'Charts and totals.' },
    { key: 'milestones', label: 'Milestones', description: 'Badges for the totals you have reached.' },
    { key: 'favouriteLists', label: 'Favourited lists', description: 'Community lists you favourited. Lists you publish are always public.' },
  ];

  const fieldRows: { key: ProfileFieldKey; label: string }[] = [
    { key: 'wordsKnown', label: 'Words known' },
    { key: 'kanjiKnown', label: 'Kanji known' },
    { key: 'characters', label: 'Characters' },
    { key: 'titlesFinished', label: 'Titles finished' },
    { key: 'memberSince', label: 'Member since' },
    { key: 'links', label: 'Links' },
    { key: 'currentlyReading', label: 'In progress' },
  ];

  function fieldShown(key: ProfileFieldKey) {
    return !privacy.value?.hiddenFields.includes(key);
  }

  function setFieldShown(key: ProfileFieldKey, shown: boolean) {
    if (!privacy.value) return;
    const hidden = new Set(privacy.value.hiddenFields);
    if (shown) hidden.delete(key);
    else hidden.add(key);
    privacy.value.hiddenFields = [...hidden];
  }

  const isPrivate = computed(() => privacy.value?.profile === 'private');

  onBeforeRouteLeave(() => {
    if (!isDirty.value) return true;
    return new Promise<boolean>((resolve) => {
      let settled = false;
      const done = (value: boolean) => {
        if (settled) return;
        settled = true;
        resolve(value);
      };
      confirm.require({
        header: 'Leave without saving?',
        message: 'Your unsaved profile changes will be lost.',
        acceptLabel: 'Leave',
        rejectLabel: 'Stay',
        acceptClass: 'p-button-danger',
        accept: () => done(true),
        reject: () => done(false),
        onHide: () => done(false),
      });
    });
  });

  function onBeforeUnload(event: BeforeUnloadEvent) {
    if (isDirty.value) event.preventDefault();
  }
  onMounted(() => window.addEventListener('beforeunload', onBeforeUnload));
  onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload));

  const saving = ref(false);
  async function saveProfile() {
    if (bioTooLong.value || !avatarKanjiValid.value || !isDirty.value) return;
    saving.value = true;
    try {
      header.value = await $api<ProfileHeader>('user/profile', { method: 'PATCH', body: formPayload() });
      if (auth.user)
        auth.user = { ...auth.user, avatarKanji: header.value.avatarKanji, avatarColour: header.value.avatarColour, avatarStyle: header.value.avatarStyle };
      toast.add({ severity: 'success', summary: 'Profile saved', life: 2500 });
    } catch (err: unknown) {
      showErrorToast(toast, apiErrorMessage(err, "Couldn't save your profile"));
    } finally {
      saving.value = false;
    }
  }

  const strokesByCharacter = ref<Record<string, string[]>>({});
  const requestedStrokes = new Set<string>();

  /** Asks once per character; ones without KanjiVG data stay absent and draw filled. */
  async function loadStrokes(characters: string[]) {
    const missing = characters.filter((c) => !requestedStrokes.has(c));
    if (!missing.length) return;
    missing.forEach((c) => requestedStrokes.add(c));
    try {
      const found = await $api<Record<string, string[]>>('kanji/strokes', { query: { characters: missing.join('') } });
      strokesByCharacter.value = { ...strokesByCharacter.value, ...found };
    } catch {
      missing.forEach((c) => requestedStrokes.delete(c));
    }
  }

  const currentCharacter = computed(() => avatarCharacter(avatarKanji.value.trim() || null, name.value));
  onMounted(() => {
    watch(currentCharacter, (c) => void loadStrokes([c]), { immediate: true });
    watch(
      avatarStyle,
      (style) => {
        if (style === 'stroke') void loadStrokes(PREMADE_AVATARS.map((p) => p.kanji));
      },
      { immediate: true }
    );
  });

  function pickPremade(kanji: string, colour: ProfileColour) {
    avatarKanji.value = kanji;
    avatarColour.value = colour;
  }

  /** Moves the item at index by delta, or drops it when delta is null; null when the move falls off either end. */
  function reordered<T>(items: T[], index: number, delta: number | null): T[] | null {
    if (delta === null) return items.filter((_, i) => i !== index);
    const target = index + delta;
    if (target < 0 || target >= items.length) return null;
    const next = [...items];
    [next[index], next[target]] = [next[target]!, next[index]!];
    return next;
  }

  const pinned = computed(() => pinnedData.value ?? []);
  const pinBusy = ref(false);
  const pinPickerOpen = ref(false);

  async function editPins(index: number, delta: number | null) {
    const next = reordered(pinned.value, index, delta);
    if (!next) return;
    pinBusy.value = true;
    if (await pins.reorder(next.map((p) => p.deckId))) pinnedData.value = next;
    pinBusy.value = false;
  }

  const favouriteWords = computed(() => wordsData.value ?? []);
  const wordBusy = ref(false);

  async function saveWords(words: Pick<ProfileFavouriteWord, 'wordId' | 'readingIndex'>[]) {
    wordBusy.value = true;
    try {
      wordsData.value = await $api<ProfileFavouriteWord[]>('user/profile/favourite-words', {
        method: 'PUT',
        body: { words: words.map(({ wordId, readingIndex }) => ({ wordId, readingIndex })) },
      });
    } catch (err: unknown) {
      showErrorToast(toast, apiErrorMessage(err, "Couldn't update your favourite words"));
    } finally {
      wordBusy.value = false;
    }
  }

  function editWords(index: number, delta: number | null) {
    const next = reordered(favouriteWords.value, index, delta);
    if (next) void saveWords(next);
  }

  const wordQuery = ref('');
  const { results: searchResults, isLoading: searching, search, clearResults } = useDictionarySearch();
  const wordResults = computed(() => searchResults.value.slice(0, 8));
  watch(wordQuery, (q) => search(q, 8));

  async function addWord(entry: DictionaryEntry) {
    if (favouriteWords.value.length >= MAX_WORDS) return;
    if (favouriteWords.value.some((w) => w.wordId === entry.wordId && w.readingIndex === entry.readingIndex)) return;
    await saveWords([...favouriteWords.value, entry]);
    wordQuery.value = '';
    clearResults();
  }

  onMounted(async () => {
    if (!header.value) await refreshHeader();
  });
</script>

<template>
  <div class="container mx-auto flex flex-col gap-4 p-2 md:p-4">
    <div class="flex flex-wrap items-center gap-2">
      <NuxtLink to="/settings" aria-label="Back to settings" class="rounded-full">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded tabindex="-1" />
      </NuxtLink>
      <div class="min-w-0 flex-1">
        <h1 class="text-2xl font-bold">Profile</h1>
        <p class="text-sm text-muted-color">Customise your profile and what will be shown to others.</p>
      </div>
      <Button
        v-if="handle"
        as="router-link"
        :to="profilePath(handle)"
        label="View profile"
        icon="pi pi-external-link"
        severity="secondary"
        outlined
        size="small"
      />
    </div>

    <Message v-if="header && !header.displayName" severity="warn" :closable="false">
      Your profile stays private until you
      <NuxtLink to="/settings/account" class="font-semibold underline">choose a display name</NuxtLink>. It's the name others see and the address of your
      profile.
    </Message>

    <form v-if="header && privacy" class="flex flex-col gap-5" @submit.prevent="saveProfile">
      <Card>
        <template #title><h2 class="text-lg font-semibold">Appearance</h2></template>
        <template #content>
          <div class="overflow-hidden rounded-lg border border-surface-200 dark:border-surface-700" aria-hidden="true">
            <div class="flex h-28 items-end gap-3 px-4 pt-6" :style="{ background: headerBackground(headerColour, avatarColour, name) }">
              <ProfileAvatar
                :name="name"
                :kanji="avatarKanji || null"
                :colour="avatarColour"
                :avatar-style="avatarStyle"
                :strokes="strokesByCharacter[currentCharacter]"
                :size="72"
                framed
                class="-mb-6"
              />
              <span class="pb-3 text-lg font-bold text-white" v-bind="japaneseTextAttrs(name)">{{ name }}</span>
            </div>
            <div class="h-6 bg-surface-0 dark:bg-surface-900" />
          </div>

          <div class="mt-5 flex flex-col gap-5">
            <fieldset class="flex flex-col gap-2">
              <legend class="mb-1 font-medium">Avatar</legend>
              <div class="grid max-w-3xl grid-cols-8 gap-2 sm:grid-cols-12">
                <button
                  v-for="item in PREMADE_AVATARS"
                  :key="`${item.kanji}-${item.colour}`"
                  type="button"
                  class="aspect-square rounded-md outline-offset-2 focus-visible:outline-2 focus-visible:outline-primary-500"
                  :class="{ [SELECTED_RING]: avatarKanji === item.kanji && avatarColour === item.colour }"
                  :aria-label="`Use ${item.kanji} on ${item.colour}`"
                  :aria-pressed="avatarKanji === item.kanji && avatarColour === item.colour"
                  @click="pickPremade(item.kanji, item.colour)"
                >
                  <ProfileAvatar
                    :name="null"
                    :kanji="item.kanji"
                    :colour="item.colour"
                    :avatar-style="avatarStyle"
                    :strokes="strokesByCharacter[item.kanji]"
                    :size="40"
                    class="!h-full !w-full"
                    aria-hidden="true"
                  />
                </button>
              </div>
              <div class="mt-2 flex flex-wrap items-end gap-4">
                <div class="flex flex-col gap-1">
                  <label for="avatar-kanji" class="text-sm">Your own character</label>
                  <InputText
                    id="avatar-kanji"
                    v-model="avatarKanji"
                    maxlength="2"
                    class="w-20 text-center text-lg"
                    lang="ja"
                    :invalid="showAvatarKanjiError"
                    aria-describedby="avatar-kanji-help"
                  />
                </div>
                <small
                  id="avatar-kanji-help"
                  class="pb-2"
                  :class="showAvatarKanjiError ? 'text-red-600 dark:text-red-400' : 'text-muted-color'"
                  aria-live="polite"
                >
                  {{ showAvatarKanjiError ? 'Pick a single kanji or kana.' : 'One kanji or kana. Leave empty to use the first letter of your name.' }}
                </small>
              </div>
              <span id="avatar-style-label" class="mt-2 text-sm">Style</span>
              <div class="grid grid-cols-3 gap-2 sm:grid-cols-6" role="radiogroup" aria-labelledby="avatar-style-label">
                <button
                  v-for="option in AVATAR_STYLES"
                  :key="option.value"
                  type="button"
                  role="radio"
                  :aria-checked="avatarStyle === option.value"
                  class="flex min-w-0 items-center gap-2 rounded-md border border-surface-200 py-1.5 pl-1.5 pr-2 text-sm dark:border-surface-700 outline-offset-2 focus-visible:outline-2 focus-visible:outline-primary-500"
                  :class="{ [SELECTED_RING]: avatarStyle === option.value }"
                  @click="avatarStyle = option.value"
                >
                  <ProfileAvatar
                    :name="name"
                    :kanji="avatarKanji || null"
                    :colour="avatarColour"
                    :avatar-style="option.value"
                    :strokes="strokesByCharacter[currentCharacter]"
                    :size="44"
                    aria-hidden="true"
                  />
                  <span class="truncate">{{ option.label }}</span>
                </button>
              </div>
              <span id="avatar-colour-label" class="mt-2 text-sm">Avatar colour</span>
              <div class="flex flex-wrap gap-2" role="radiogroup" aria-labelledby="avatar-colour-label">
                <button
                  v-for="colour in PROFILE_COLOURS"
                  :key="colour"
                  type="button"
                  role="radio"
                  :aria-checked="(avatarColour ?? null) === colour"
                  :aria-label="colour"
                  class="h-8 w-8 rounded-full outline-offset-2 focus-visible:outline-2 focus-visible:outline-primary-500"
                  :class="{ [SELECTED_RING]: avatarColour === colour }"
                  :style="{ background: PROFILE_PALETTE[colour].avatar }"
                  @click="avatarColour = colour"
                />
              </div>
            </fieldset>

            <fieldset class="flex flex-col gap-2">
              <legend id="banner-colour-label" class="mb-1 font-medium">Banner colour</legend>
              <div class="flex flex-wrap gap-2" role="radiogroup" aria-labelledby="banner-colour-label">
                <button
                  type="button"
                  role="radio"
                  :aria-checked="headerColour === null"
                  class="h-8 rounded-md px-2.5 text-xs font-medium text-white outline-offset-2 focus-visible:outline-2 focus-visible:outline-primary-500"
                  :class="{ [SELECTED_RING]: headerColour === null }"
                  :style="{ background: headerBackground(null, avatarColour, name) }"
                  @click="headerColour = null"
                >
                  Match avatar
                </button>
                <button
                  v-for="colour in PROFILE_COLOURS"
                  :key="colour"
                  type="button"
                  role="radio"
                  :aria-checked="headerColour === colour"
                  :aria-label="colour"
                  class="h-8 w-12 rounded-md outline-offset-2 focus-visible:outline-2 focus-visible:outline-primary-500"
                  :class="{ [SELECTED_RING]: headerColour === colour }"
                  :style="{ background: PROFILE_PALETTE[colour].header }"
                  @click="headerColour = colour"
                />
              </div>
            </fieldset>
          </div>
        </template>
      </Card>

      <Card>
        <template #title><h2 class="text-lg font-semibold">About</h2></template>
        <template #content>
          <div class="flex flex-col gap-5">
            <div class="flex flex-col gap-1.5">
              <div class="flex items-baseline justify-between">
                <label for="profile-bio" class="font-medium">Bio</label>
                <span class="text-xs tabular-nums" :class="bioTooLong ? 'text-red-600 dark:text-red-400' : 'text-muted-color'"
                  >{{ bio.trim().length }} / {{ BIO_LIMIT }}</span
                >
              </div>
              <Textarea
                id="profile-bio"
                v-model="bio"
                rows="3"
                auto-resize
                :invalid="bioTooLong"
                placeholder="Tell people a bit about yourself: what got you into Japanese, what you like to read or watch. Keep it friendly. You can add links below."
              />
            </div>
            <fieldset class="flex flex-col gap-2" aria-describedby="profile-links-hint">
              <legend class="font-medium">Links</legend>
              <p id="profile-links-hint" class="mb-2 mt-0.5 text-sm text-muted-color">Your socials, blog or anything else you'd like people to find. Up to {{ MAX_LINKS }}.</p>
              <div v-for="(_, i) in links" :key="i" class="flex items-center gap-2">
                <InputText v-model="links[i]" type="url" inputmode="url" placeholder="https://" class="flex-1" :aria-label="`Link ${i + 1}`" />
                <Button icon="pi pi-times" text rounded severity="secondary" :aria-label="`Remove link ${i + 1}`" @click="links.splice(i, 1)" />
              </div>
              <Button v-if="links.length < MAX_LINKS" label="Add a link" icon="pi pi-plus" text size="small" class="self-start" @click="links.push('')" />
            </fieldset>
          </div>
        </template>
      </Card>

      <Card>
        <template #title><h2 class="text-lg font-semibold">Visibility</h2></template>
        <template #content>
          <div class="flex flex-col gap-6">
            <fieldset class="flex flex-col gap-2">
              <legend class="mb-1 font-medium">Profile</legend>
              <div v-for="option in visibilityOptions" :key="option.value" class="flex items-start gap-2">
                <RadioButton
                  v-model="privacy.profile"
                  :input-id="`visibility-${option.value}`"
                  :value="option.value"
                  :disabled="!header.displayName && option.value !== 'private'"
                />
                <label :for="`visibility-${option.value}`" class="flex flex-col">
                  <span>{{ option.label }}</span>
                  <small class="text-muted-color">{{ option.description }}</small>
                </label>
              </div>
            </fieldset>

            <div class="flex flex-col gap-1" :class="{ 'opacity-60': isPrivate }">
              <h3 class="font-medium">Sections</h3>
              <p v-if="isPrivate" class="text-sm text-muted-color">Your profile is private, so every section is hidden from others.</p>
              <div
                v-for="row in sectionRows"
                :key="row.key"
                class="flex flex-wrap items-center justify-between gap-2 border-t border-surface-200 py-2.5 first-of-type:border-t-0 dark:border-surface-700"
              >
                <div class="min-w-0 flex-1">
                  <div :id="`section-${row.key}`">{{ row.label }}</div>
                  <small class="text-muted-color">{{ row.description }}</small>
                </div>
                <SelectButton
                  v-model="privacy.sections[row.key]"
                  :options="audienceOptions"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                  :disabled="isPrivate"
                  :aria-labelledby="`section-${row.key}`"
                  size="small"
                />
              </div>
            </div>

            <fieldset class="flex flex-col gap-2" :class="{ 'opacity-60': isPrivate }">
              <legend class="mb-1 font-medium">Show on my profile</legend>
              <div class="grid gap-2 sm:grid-cols-2">
                <div v-for="field in fieldRows" :key="field.key" class="flex items-center gap-2">
                  <Checkbox
                    :model-value="fieldShown(field.key)"
                    binary
                    :input-id="`field-${field.key}`"
                    :disabled="isPrivate"
                    @update:model-value="setFieldShown(field.key, $event)"
                  />
                  <label :for="`field-${field.key}`">{{ field.label }}</label>
                </div>
              </div>
            </fieldset>

            <div class="flex items-center justify-between gap-3">
              <label for="hide-adult" class="flex flex-col">
                <span class="font-medium">Hide adult titles from others</span>
                <small class="text-muted-color">Titles in the Adult Only genre stay visible to you.</small>
              </label>
              <ToggleSwitch v-model="privacy.hideAdultTitles" input-id="hide-adult" />
            </div>
          </div>
        </template>
      </Card>

      <div
        class="sticky bottom-2 z-10 flex items-center justify-end gap-2 rounded-lg border border-surface-200 bg-surface-0 p-2 dark:border-surface-700 dark:bg-surface-900"
      >
        <span class="mr-auto pl-2 text-sm text-muted-color" aria-live="polite">{{ isDirty ? 'Unsaved changes' : 'All changes saved' }}</span>
        <Button label="Discard changes" severity="secondary" text :class="{ invisible: !isDirty }" @click="resetForm" />
        <Button type="submit" label="Save profile" :loading="saving" :disabled="!isDirty || bioTooLong || !avatarKanjiValid" />
      </div>
    </form>

    <Card id="pinned">
      <template #title><h2 class="text-lg font-semibold">Pinned titles</h2></template>
      <template #subtitle>
        <span class="text-sm">Up to {{ MAX_PROFILE_PINS }}. Add them here, or pin a title from its page or your library.</span>
      </template>
      <template #content>
        <p v-if="!pinned.length" class="text-sm text-muted-color">Nothing pinned yet.</p>
        <ol v-else class="flex flex-col">
          <li
            v-for="(item, i) in pinned"
            :key="item.deckId"
            class="flex items-center gap-3 border-t border-surface-200 py-2 first:border-t-0 dark:border-surface-700"
          >
            <ProfileDeckRow :deck="item" />
            <ProfileReorderButtons
              :label="localiseTitle(item)"
              :index="i"
              :count="pinned.length"
              :disabled="pinBusy"
              remove-verb="Unpin"
              @move="editPins(i, $event)"
            />
          </li>
        </ol>
        <Button v-if="handle" label="Add a title" icon="pi pi-plus" size="small" severity="secondary" class="mt-3" @click="pinPickerOpen = true" />
        <ProfilePinPicker v-if="handle" v-model:visible="pinPickerOpen" @changed="refreshPinned()" />
      </template>
    </Card>

    <Card id="favourite-words">
      <template #title><h2 class="text-lg font-semibold">Favourite words</h2></template>
      <template #subtitle
        ><span class="text-sm">Up to {{ MAX_WORDS }}.</span></template
      >
      <template #content>
        <ol v-if="favouriteWords.length" class="mb-4 flex flex-col">
          <li
            v-for="(word, i) in favouriteWords"
            :key="`${word.wordId}-${word.readingIndex}`"
            class="flex items-center gap-3 border-t border-surface-200 py-2 first:border-t-0 dark:border-surface-700"
          >
            <ProfileFavouriteWord :word="word" />
            <ProfileReorderButtons
              :label="word.text"
              :index="i"
              :count="favouriteWords.length"
              :disabled="wordBusy"
              remove-verb="Remove"
              @move="editWords(i, $event)"
            />
          </li>
        </ol>

        <div v-if="favouriteWords.length < MAX_WORDS" class="flex flex-col gap-2">
          <label for="word-search" class="text-sm font-medium">Add a word</label>
          <IconField>
            <InputIcon :class="searching ? 'pi pi-spin pi-spinner' : 'pi pi-search'" />
            <InputText id="word-search" v-model="wordQuery" placeholder="Search in Japanese or English" class="w-full" autocomplete="off" />
          </IconField>
          <ul v-if="wordResults.length" class="flex flex-col rounded-lg border border-surface-200 dark:border-surface-700">
            <li
              v-for="entry in wordResults"
              :key="`${entry.wordId}-${entry.readingIndex}`"
              class="border-t border-surface-200 first:border-t-0 dark:border-surface-700"
            >
              <button
                type="button"
                class="flex w-full items-baseline gap-3 px-3 py-2 text-left hover:bg-surface-100 focus-visible:bg-surface-100 dark:hover:bg-surface-800 dark:focus-visible:bg-surface-800"
                :disabled="wordBusy"
                @click="addWord(entry)"
              >
                <ProfileFavouriteWord :word="{ text: entry.text, reading: null, meaning: entry.meanings.slice(0, 2).join(', ') }" />
                <i class="pi pi-plus text-xs text-primary-600 dark:text-primary-400" aria-hidden="true" />
              </button>
            </li>
          </ul>
          <p v-else-if="wordQuery.trim() && !searching" class="text-sm text-muted-color">No words found.</p>
        </div>
      </template>
    </Card>
  </div>
</template>
