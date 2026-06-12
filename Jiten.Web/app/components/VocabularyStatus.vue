<script setup lang="ts">
  import Button from 'primevue/button';
  import Popover from 'primevue/popover';
  import { KnownState, StudyDeckType, type Word } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';
  import { storeToRefs } from 'pinia';

  const { $api } = useNuxtApp();
  const auth = useAuthStore();
  const srsStore = useSrsStore();
  const toast = useToast();
  const { quickMasterVocabulary } = storeToRefs(useJitenStore());

  const props = defineProps<{
    word: Word;
    knownStatesOverride?: KnownState[];
  }>();

  const knownStates = ref([...(props.knownStatesOverride ?? props.word.knownStates ?? [])]);
  const op = ref();
  const opActivated = ref(false);
  const deckOpActivated = ref(false);
  const addingToDeck = ref<number | null>(null);

  watch([() => props.knownStatesOverride, () => props.word.knownStates], ([override, wordStates]) => {
    knownStates.value = [...(override ?? wordStates ?? [])];
  });

  const wordPath = computed(() => `${props.word.wordId}/${props.word.mainReading.readingIndex}`);

  const isBlacklisted = computed(() => knownStates.value.includes(KnownState.Blacklisted));
  const isRedundant = computed(() => knownStates.value.includes(KnownState.Redundant));
  const isSuspended = computed(() => knownStates.value.includes(KnownState.Suspended));

  const staticDecks = computed(() =>
    srsStore.studyDecks.filter(d => d.deckType === StudyDeckType.StaticWordList)
  );

  const redundantTooltip = computed(() => {
    if (knownStates.value.includes(KnownState.Mastered)) return 'Known via another form of this word (Mastered)';
    if (knownStates.value.includes(KnownState.Mature)) return 'Known via another form of this word (Mature)';
    if (knownStates.value.includes(KnownState.Young)) return 'Known via another form of this word (Young)';
    if (knownStates.value.includes(KnownState.Blacklisted)) return 'Known via another form of this word (Blacklisted)';
    return 'Covered by another form of this word in your deck (not yet studied)';
  });

  const masterWord = async () => {
    op.value?.hide();
    try {
      await $api<boolean>(`user/vocabulary/add/${wordPath.value}`, { method: 'POST' });
      knownStates.value = [KnownState.Mastered];
    }
    catch { /* state unchanged on failure */ }
  };

  const blacklistWord = async () => {
    op.value?.hide();
    try {
      await $api<boolean>(`user/vocabulary/blacklist/${wordPath.value}`, { method: 'POST' });
      knownStates.value = [KnownState.Blacklisted];
    }
    catch { /* state unchanged on failure */ }
  };

  const addToDeck = async (deckId: number) => {
    addingToDeck.value = deckId;
    try {
      await srsStore.addDeckWord(deckId, props.word.wordId, props.word.mainReading.readingIndex, 1);
      toast.add({ severity: 'success', summary: `Added to deck`, life: 1500 });
      deckOp.value?.hide();
    } catch (e: any) {
      const msg = e?.data?.message || e?.message || '';
      if (msg.includes('already in the deck')) {
        toast.add({ severity: 'info', summary: 'Already in deck', life: 2000 });
      } else {
        toast.add({ severity: 'error', summary: 'Failed to add', life: 3000 });
      }
    } finally {
      addingToDeck.value = null;
    }
  };

  const deckOp = ref();
  const loadingDecks = ref(false);

  const onPlusClick = async (e: MouseEvent) => {
    if (e.ctrlKey) blacklistWord();
    else if (e.shiftKey || quickMasterVocabulary.value) masterWord();
    else {
      opActivated.value = true;
      await nextTick();
      op.value?.toggle(e);
    }
  };

  const onDeckMenuClick = async (e: MouseEvent) => {
    deckOpActivated.value = true;
    await nextTick();
    deckOp.value?.toggle(e);
    if (srsStore.studyDecks.length === 0) {
      loadingDecks.value = true;
      await srsStore.fetchStudyDecks();
      loadingDecks.value = false;
    }
  };

  const removeWord = async () => {
    try {
      await $api<boolean>(`user/vocabulary/remove/${wordPath.value}`, { method: 'POST' });
      knownStates.value = [KnownState.New];
    }
    catch { /* state unchanged on failure */ }
  };
</script>

<template>
  <ClientOnly>
    <span class="inline-flex items-center gap-1">
      <template v-if="auth.isAuthenticated">
        <template v-if="isRedundant">
          <Tooltip :content="redundantTooltip">
            <span class="text-blue-500 dark:text-blue-300 cursor-default">Redundant</span>
          </Tooltip>
        </template>
        <template v-else-if="isSuspended">
          <Tooltip content="Paused — retains its scheduling but is not due for review">
            <span class="text-gray-600 dark:text-gray-300 cursor-default">Suspended</span>
          </Tooltip>
          <Button icon="pi pi-minus" size="small" text severity="danger" @click="removeWord" />
        </template>
        <template v-else-if="knownStates.includes(KnownState.Mature)">
          <span class="text-green-600 dark:text-green-300">Mature</span>
          <Button icon="pi pi-minus" size="small" text severity="danger" @click="removeWord" />
        </template>
        <template v-else-if="knownStates.includes(KnownState.Mastered)">
          <span class="text-green-600 dark:text-green-300">Mastered</span>
          <Button icon="pi pi-minus" size="small" text severity="danger" @click="removeWord" />
        </template>
        <template v-else-if="knownStates.includes(KnownState.Young)">
          <span class="text-yellow-600 dark:text-yellow-300">Young</span>
          <Button icon="pi pi-minus" size="small" text severity="danger" @click="removeWord" />
        </template>
        <template v-else-if="isBlacklisted">
          <span class="text-gray-600 dark:text-gray-300">Blacklisted</span>
          <Button icon="pi pi-minus" size="small" text severity="danger" @click="removeWord" />
        </template>
        <template v-else>
          <Tooltip :content="(quickMasterVocabulary ? 'Click: Master\nCtrl+Click: Blacklist' : 'Shift+Click: Master\nCtrl+Click: Blacklist') + '\n(Change in the quick cog settings with the Master in 1 click option)'">
            <Button icon="pi pi-plus" size="small" text severity="success" @click="onPlusClick" />
          </Tooltip>
        </template>
        <Popover v-if="opActivated" ref="op" :pt="{ content: { class: 'p-1' } }">
          <div class="flex flex-col">
            <button class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm text-green-600 hover:bg-green-50 dark:hover:bg-green-900/20 cursor-pointer" @click="masterWord">
              <i class="pi pi-check w-4 text-center" /><span>Master</span>
            </button>
            <button class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm text-surface-600 dark:text-surface-400 hover:bg-surface-100 dark:hover:bg-surface-700 cursor-pointer" @click="blacklistWord">
              <i class="pi pi-ban w-4 text-center" /><span>Blacklist</span>
            </button>
          </div>
        </Popover>
        <Button icon="pi pi-ellipsis-h" size="small" text severity="secondary" @click="onDeckMenuClick" />
        <Popover v-if="deckOpActivated" ref="deckOp" :pt="{ content: { class: 'p-1' } }">
          <div class="flex flex-col">
            <span class="px-3 py-1 text-xs font-semibold text-surface-400 uppercase tracking-wide">Add to deck</span>
            <div v-if="loadingDecks" class="flex justify-center py-2">
              <i class="pi pi-spin pi-spinner text-surface-400" />
            </div>
            <template v-else-if="staticDecks.length > 0">
              <button
                v-for="deck in staticDecks"
                :key="deck.userStudyDeckId"
                class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm text-surface-700 dark:text-surface-300 hover:bg-surface-100 dark:hover:bg-surface-700 cursor-pointer"
                :disabled="addingToDeck === deck.userStudyDeckId"
                @click="addToDeck(deck.userStudyDeckId)"
              >
                <i v-if="addingToDeck === deck.userStudyDeckId" class="pi pi-spin pi-spinner w-4 text-center" />
                <i v-else class="pi pi-list w-4 text-center" />
                <span class="truncate max-w-40">{{ deck.name }}</span>
              </button>
            </template>
            <span v-else class="px-3 py-1.5 text-sm text-surface-400 italic">No word list decks</span>
            <div class="border-t border-surface-200 dark:border-surface-700 my-1" />
            <NuxtLink
              :to="`/vocabulary/${word.wordId}/${word.mainReading.readingIndex}/reviews`"
              class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm text-surface-700 dark:text-surface-300 hover:bg-surface-100 dark:hover:bg-surface-700 cursor-pointer w-full"
              @click="deckOp?.hide()"
            >
              <i class="pi pi-history w-4 text-center" />
              <span>Review history</span>
            </NuxtLink>
          </div>
        </Popover>
        <span aria-hidden="true">|</span>
      </template>
    </span>
    <template #fallback>
      <span class="inline-flex items-center gap-1" aria-hidden="true"></span>
    </template>
  </ClientOnly>
</template>
