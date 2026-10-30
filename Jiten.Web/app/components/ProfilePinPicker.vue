<script setup lang="ts">
  import type { MediaSuggestion } from '~/types';
  import { normaliseTitleQuery, titleMatches } from '~/utils/titleSearch';

  const RESULT_LIMIT = 50;

  type LibraryRow = MediaSuggestion & { isPrivate: boolean };

  const visible = defineModel<boolean>('visible', { required: true });
  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const localiseTitle = useLocaliseTitle();
  const pins = useProfilePins();

  const library = ref<LibraryRow[] | null>(null);
  const loadFailed = ref(false);
  const query = ref('');
  const busyId = ref<number | null>(null);
  const changed = ref(false);

  watch(
    visible,
    async (open) => {
      if (!open) {
        if (changed.value) emit('changed');
        changed.value = false;
        return;
      }
      query.value = '';
      if (library.value) return;
      loadFailed.value = false;
      try {
        [library.value] = await Promise.all([$api<LibraryRow[]>('user/media-list'), pins.ensureLoaded()]);
      } catch {
        loadFailed.value = true;
      }
    },
    { immediate: true }
  );

  /** A hidden title can't be pinned, but one pinned before it was hidden stays listed so it can be unpinned. */
  const results = computed(() => {
    const key = normaliseTitleQuery(query.value);
    return (library.value ?? []).filter((d) => (!d.isPrivate || pins.isPinned(d.deckId)) && titleMatches(d, key)).slice(0, RESULT_LIMIT);
  });

  async function toggle(deck: LibraryRow) {
    busyId.value = deck.deckId;
    const done = pins.isPinned(deck.deckId) ? await pins.unpin(deck.deckId) : await pins.pin(deck.deckId, localiseTitle(deck));
    if (done) changed.value = true;
    busyId.value = null;
  }
</script>

<template>
  <Dialog v-model:visible="visible" modal header="Pin titles to your profile" :style="{ width: '34rem' }" :breakpoints="{ '575px': '95vw' }" :draggable="false">
    <div class="flex flex-col gap-3">
      <IconField>
        <InputIcon class="pi pi-search" />
        <InputText v-model="query" placeholder="Search your library" aria-label="Search your library" class="w-full" autofocus />
      </IconField>
      <p class="text-xs text-muted-color">Up to {{ MAX_PROFILE_PINS }} titles. Hidden titles can't be pinned.</p>

      <p v-if="loadFailed" class="py-6 text-center text-sm text-muted-color">Couldn't load your library.</p>
      <div v-else-if="library === null" class="flex flex-col gap-1" aria-hidden="true">
        <Skeleton v-for="i in 5" :key="i" height="3.5rem" />
      </div>
      <p v-else-if="!results.length" class="py-6 text-center text-sm text-muted-color">
        {{ query ? 'No titles in your library match that search.' : 'Your library is empty. Set a status on a title to pin it.' }}
      </p>
      <ul v-else class="-mx-2 flex max-h-[50vh] flex-col overflow-y-auto">
        <li v-for="deck in results" :key="deck.deckId">
          <button
            type="button"
            class="flex w-full items-center gap-3 rounded-md px-2 py-1.5 text-left transition-colors hover:bg-surface-100 focus-visible:bg-surface-100 disabled:opacity-60 dark:hover:bg-surface-800 dark:focus-visible:bg-surface-800"
            :disabled="busyId !== null"
            :aria-pressed="pins.isPinned(deck.deckId)"
            @click="toggle(deck)"
          >
            <ProfileDeckRow :deck="deck" />
            <i
              class="pi shrink-0 text-sm"
              :class="
                busyId === deck.deckId ? 'pi-spin pi-spinner text-muted-color' : pins.isPinned(deck.deckId) ? 'pi-thumbtack text-primary-600 dark:text-primary-400' : 'pi-plus text-muted-color'
              "
              aria-hidden="true"
            />
          </button>
        </li>
      </ul>
    </div>
    <template #footer>
      <Button label="Done" @click="visible = false" />
    </template>
  </Dialog>
</template>
