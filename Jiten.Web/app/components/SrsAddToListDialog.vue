<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';
  import { DeckDownloadType, DeckOrder, StudyDeckType } from '~/types';

  const props = defineProps<{
    visible: boolean;
    cards: { wordId: number; readingIndex: number }[];
  }>();

  const emit = defineEmits(['update:visible', 'added']);

  const srsStore = useSrsStore();
  const toast = useToast();

  const localVisible = ref(props.visible);
  watch(() => props.visible, (v) => { localVisible.value = v; });
  watch(localVisible, (v) => { emit('update:visible', v); });

  const loadingDecks = ref(false);
  const addingTo = ref<number | null>(null);
  const creating = ref(false);
  const showCreate = ref(false);
  const newName = ref('');

  const staticDecks = computed(() =>
    srsStore.studyDecks.filter(d => d.deckType === StudyDeckType.StaticWordList)
  );

  const count = computed(() => props.cards.length);

  watch(localVisible, async (v) => {
    if (v) {
      showCreate.value = false;
      newName.value = '';
      if (srsStore.studyDecks.length === 0) {
        loadingDecks.value = true;
        await srsStore.fetchStudyDecks();
        loadingDecks.value = false;
      }
    }
  });

  async function addWordsTo(deckId: number) {
    if (count.value === 1) {
      const c = props.cards[0];
      await srsStore.addDeckWord(deckId, c.wordId, c.readingIndex, 1);
      return { added: 1, updated: 0 };
    }
    return await srsStore.addDeckWordsBatch(deckId, props.cards);
  }

  async function addToList(deckId: number) {
    addingTo.value = deckId;
    try {
      const res = await addWordsTo(deckId);
      const added = res?.added ?? count.value;
      toast.add({
        severity: 'success',
        summary: count.value === 1 ? 'Added to list' : `Added ${added} card${added === 1 ? '' : 's'}`,
        life: 2000,
      });
      emit('added');
      localVisible.value = false;
    } catch {
      toast.add({ severity: 'error', summary: 'Failed to add', detail: 'Could not add to the word list', life: 4000 });
    } finally {
      addingTo.value = null;
    }
  }

  async function createAndAdd() {
    const name = newName.value.trim();
    if (!name) return;
    creating.value = true;
    try {
      const { userStudyDeckId } = await srsStore.addStudyDeck({
        deckType: StudyDeckType.StaticWordList,
        name,
        downloadType: DeckDownloadType.Full,
        order: DeckOrder.ImportOrder,
        minFrequency: 0,
        maxFrequency: 0,
        excludeKana: false,
      });
      await addToList(userStudyDeckId);
    } catch {
      toast.add({ severity: 'error', summary: 'Failed to create list', life: 4000 });
    } finally {
      creating.value = false;
    }
  }
</script>

<template>
  <Dialog
    v-model:visible="localVisible"
    modal
    :header="count === 1 ? 'Add to word list' : `Add ${count} cards to word list`"
    :style="{ width: '28rem' }"
    :breakpoints="{ '500px': '95vw' }"
    :dismissable-mask="addingTo === null && !creating"
  >
    <div class="flex flex-col gap-3">
      <div v-if="loadingDecks" class="flex justify-center py-6">
        <i class="pi pi-spin pi-spinner text-2xl text-surface-400" />
      </div>

      <template v-else>
        <p class="text-sm text-surface-500">
          {{ count === 1 ? 'Choose a list to add this card to.' : `Choose a list to add the ${count} selected cards to.` }}
        </p>

        <div v-if="staticDecks.length > 0" class="flex flex-col gap-1 max-h-64 overflow-y-auto">
          <button
            v-for="deck in staticDecks"
            :key="deck.userStudyDeckId"
            class="flex items-center gap-2 rounded-lg border border-surface-200 dark:border-surface-700 px-3 py-2.5 text-sm text-left hover:bg-surface-100 dark:hover:bg-surface-800 transition-colors disabled:opacity-60"
            :disabled="addingTo !== null || creating"
            @click="addToList(deck.userStudyDeckId)"
          >
            <i v-if="addingTo === deck.userStudyDeckId" class="pi pi-spin pi-spinner shrink-0" />
            <i v-else class="pi pi-list text-surface-400 shrink-0" />
            <span class="flex-1 truncate font-medium">{{ deck.name }}</span>
            <span class="text-xs text-surface-400 tabular-nums shrink-0">{{ deck.totalWords }}</span>
          </button>
        </div>
        <p v-else class="text-sm text-surface-400 italic">You don't have any word lists yet.</p>

        <div class="border-t border-surface-200 dark:border-surface-700 pt-3">
          <Button
            v-if="!showCreate"
            label="New list"
            icon="pi pi-plus"
            severity="secondary"
            text
            size="small"
            :disabled="addingTo !== null"
            @click="showCreate = true"
          />
          <div v-else class="flex flex-col gap-2">
            <InputText
              v-model="newName"
              placeholder="List name (e.g. Mining)"
              class="w-full"
              :maxlength="200"
              autofocus
              @keyup.enter="createAndAdd"
            />
            <div class="flex justify-end gap-2">
              <Button label="Cancel" severity="secondary" text size="small" :disabled="creating" @click="showCreate = false" />
              <Button
                label="Create & add"
                icon="pi pi-check"
                size="small"
                :loading="creating"
                :disabled="!newName.trim()"
                @click="createAndAdd"
              />
            </div>
          </div>
        </div>
      </template>
    </div>
  </Dialog>
</template>
