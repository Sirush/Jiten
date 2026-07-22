<script setup lang="ts">
  import Button from 'primevue/button';
  import Popover from 'primevue/popover';
  import { KnownState, StudyDeckType, type Word } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { storeToRefs } from 'pinia';

  const { $api } = useNuxtApp();
  const auth = useAuthStore();
  const srsStore = useSrsStore();
  const toast = useToast();
  const confirm = useConfirm();
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

  const deckMembership = ref<Set<number>>(new Set());
  const membershipLoaded = ref(false);

  const fetchDeckMembership = async () => {
    try {
      const res = await $api<{ result: number[][]; decks: number[][] }>('reader/lookup-vocabulary', {
        method: 'POST',
        body: { words: [[props.word.wordId, props.word.mainReading.readingIndex]] },
      });
      deckMembership.value = new Set(res.decks?.[0] ?? []);
    }
    catch {}
    finally { membershipLoaded.value = true; }
  };

  const decksContaining = computed(() => staticDecks.value.filter(d => deckMembership.value.has(d.userStudyDeckId)));
  const decksNotContaining = computed(() => staticDecks.value.filter(d => !deckMembership.value.has(d.userStudyDeckId)));

  const addToDeck = async (deckId: number) => {
    addingToDeck.value = deckId;
    try {
      await srsStore.addDeckWord(deckId, props.word.wordId, props.word.mainReading.readingIndex, 1);
      deckMembership.value.add(deckId);
      toast.add({ severity: 'success', summary: `Added to deck`, life: 1500 });
    } catch (e: any) {
      const msg = e?.data?.message || e?.message || '';
      if (msg.includes('already in the deck')) {
        deckMembership.value.add(deckId);
        toast.add({ severity: 'info', summary: 'Already in deck', life: 2000 });
      } else {
        toast.add({ severity: 'error', summary: 'Failed to add', life: 3000 });
      }
    } finally {
      addingToDeck.value = null;
    }
  };

  const removeFromDeck = async (deckId: number) => {
    addingToDeck.value = deckId;
    try {
      await srsStore.removeDeckWord(deckId, props.word.wordId, props.word.mainReading.readingIndex);
      deckMembership.value.delete(deckId);
      toast.add({ severity: 'success', summary: 'Removed from deck', life: 1500 });
    } catch {
      toast.add({ severity: 'error', summary: 'Failed to remove', life: 3000 });
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
    fetchDeckMembership();
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

  const plainText = computed(() => stripRubyMarkup(props.word.mainReading.text));

  const confirmForget = () => {
    confirm.require({
      message: `Forget "${plainText.value}"? This removes it from your vocabulary and deletes its review history.`,
      header: 'Forget Word',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      accept: removeWord,
    });
  };

  const srsAction = async (action: string, optimistic: KnownState[]) => {
    try {
      await $api('srs/set-vocabulary-state', {
        method: 'POST',
        body: { wordId: props.word.wordId, readingIndex: props.word.mainReading.readingIndex, state: action },
      });
      knownStates.value = optimistic;
    }
    catch { /* state unchanged on failure */ }
  };

  const resumeWord = () => srsAction('suspend-remove', [KnownState.Young]);
  const suspendWord = () => srsAction('suspend-add', [KnownState.Suspended]);
  const unmasterWord = () => srsAction('neverForget-remove', [KnownState.Young]);
  const unblacklistWord = () => srsAction('blacklist-remove', [KnownState.Young]);

  const confirmReset = () => {
    confirm.require({
      message: `Reset "${plainText.value}"? This clears its scheduling (stability, difficulty) and puts it back into Learning. Review history is kept.`,
      header: 'Reset Schedule',
      icon: 'pi pi-history',
      accept: () => srsAction('reset-schedule', [KnownState.Young]),
    });
  };

  interface StateAction {
    label: string;
    icon: string;
    danger?: boolean;
    run: () => void;
  }

  const stateActions = computed<StateAction[]>(() => {
    if (isRedundant.value) return [];
    const actions: StateAction[] = [];
    const states = knownStates.value;
    const isActive = states.includes(KnownState.Young) || states.includes(KnownState.Mature) || states.includes(KnownState.Due);

    if (isSuspended.value) {
      actions.push({ label: 'Resume', icon: 'pi pi-play', run: resumeWord });
      actions.push({ label: 'Master', icon: 'pi pi-check', run: masterWord });
      actions.push({ label: 'Blacklist', icon: 'pi pi-ban', run: blacklistWord });
    } else if (states.includes(KnownState.Mastered)) {
      actions.push({ label: 'Unmaster', icon: 'pi pi-replay', run: unmasterWord });
      actions.push({ label: 'Suspend', icon: 'pi pi-pause', run: suspendWord });
      actions.push({ label: 'Blacklist', icon: 'pi pi-ban', run: blacklistWord });
    } else if (isBlacklisted.value) {
      actions.push({ label: 'Unblacklist', icon: 'pi pi-undo', run: unblacklistWord });
      actions.push({ label: 'Master', icon: 'pi pi-check', run: masterWord });
    } else if (isActive) {
      actions.push({ label: 'Master', icon: 'pi pi-check', run: masterWord });
      actions.push({ label: 'Suspend', icon: 'pi pi-pause', run: suspendWord });
      actions.push({ label: 'Blacklist', icon: 'pi pi-ban', run: blacklistWord });
      actions.push({ label: 'Reset schedule', icon: 'pi pi-history', run: confirmReset });
    } else {
      return [];
    }

    actions.push({ label: 'Forget', icon: 'pi pi-trash', danger: true, run: confirmForget });
    return actions;
  });

  const runStateAction = (action: StateAction) => {
    deckOp.value?.hide();
    action.run();
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
          <Tooltip content="Paused, retains its scheduling but is not due for review">
            <span class="text-gray-600 dark:text-gray-300 cursor-default">Suspended</span>
          </Tooltip>
          <Tooltip content="Resume reviews">
            <Button icon="pi pi-play" size="small" text severity="success" @click="resumeWord" />
          </Tooltip>
        </template>
        <template v-else-if="knownStates.includes(KnownState.Mature)">
          <span class="text-green-600 dark:text-green-300">Mature</span>
          <Button icon="pi pi-minus" size="small" text severity="danger" @click="confirmForget" />
        </template>
        <template v-else-if="knownStates.includes(KnownState.Mastered)">
          <span class="text-green-600 dark:text-green-300">Mastered</span>
          <Button icon="pi pi-minus" size="small" text severity="danger" @click="confirmForget" />
        </template>
        <template v-else-if="knownStates.includes(KnownState.Young)">
          <span class="text-yellow-600 dark:text-yellow-300">Young</span>
          <Button icon="pi pi-minus" size="small" text severity="danger" @click="confirmForget" />
        </template>
        <template v-else-if="isBlacklisted">
          <span class="text-gray-600 dark:text-gray-300">Blacklisted</span>
          <Button icon="pi pi-minus" size="small" text severity="danger" @click="confirmForget" />
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
            <template v-if="stateActions.length > 0">
              <span class="px-3 py-1 text-xs font-semibold text-surface-400 uppercase tracking-wide">Card</span>
              <button
                v-for="action in stateActions"
                :key="action.label"
                class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm cursor-pointer"
                :class="
                  action.danger
                    ? 'text-red-600 dark:text-red-400 hover:bg-red-50 dark:hover:bg-red-900/20'
                    : 'text-surface-700 dark:text-surface-300 hover:bg-surface-100 dark:hover:bg-surface-700'
                "
                @click="runStateAction(action)"
              >
                <i :class="action.icon" class="w-4 text-center" /><span>{{ action.label }}</span>
              </button>
              <div class="border-t border-surface-200 dark:border-surface-700 my-1" />
            </template>
            <div v-if="loadingDecks || !membershipLoaded" class="flex justify-center py-2">
              <i class="pi pi-spin pi-spinner text-surface-400" />
            </div>
            <template v-else>
              <template v-if="decksContaining.length > 0">
                <span class="px-3 py-1 text-xs font-semibold text-surface-400 uppercase tracking-wide">In decks</span>
                <button
                  v-for="deck in decksContaining"
                  :key="deck.userStudyDeckId"
                  class="group flex items-center gap-2 rounded-md px-3 py-1.5 text-sm cursor-pointer text-green-600 dark:text-green-300 hover:bg-red-50 dark:hover:bg-red-900/20 hover:text-red-600 dark:hover:text-red-300"
                  title="Remove from deck"
                  :disabled="addingToDeck === deck.userStudyDeckId"
                  @click="removeFromDeck(deck.userStudyDeckId)"
                >
                  <i v-if="addingToDeck === deck.userStudyDeckId" class="pi pi-spin pi-spinner w-4 text-center" />
                  <i v-else class="pi pi-check w-4 text-center" />
                  <span class="truncate max-w-40">{{ deck.name }}</span>
                  <span class="ml-auto pl-2 text-xs invisible group-hover:visible">Remove</span>
                </button>
                <div class="border-t border-surface-200 dark:border-surface-700 my-1" />
              </template>
              <span class="px-3 py-1 text-xs font-semibold text-surface-400 uppercase tracking-wide">Add to deck</span>
              <template v-if="decksNotContaining.length > 0">
                <button
                  v-for="deck in decksNotContaining"
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
              <span v-else-if="staticDecks.length > 0" class="px-3 py-1.5 text-sm text-surface-400 italic">In all your decks</span>
              <span v-else class="px-3 py-1.5 text-sm text-surface-400 italic">No word list decks</span>
            </template>
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
