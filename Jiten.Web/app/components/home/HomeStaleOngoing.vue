<script setup lang="ts">
  import { type Deck, DeckStatus } from '~/types';
  import Popover from 'primevue/popover';
  import { useToast } from 'primevue/usetoast';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { coverUrl } from '~/utils/coverImage';
  import { closingPassStart, entryProgressPercent, formatReadDate, ongoingVerb, unitsFact } from '~/utils/mediaListEntry';
  import { formatRelativeTime } from '~/utils/relativeTime';

  type StaleDeck = Deck & {
    lastUpdatedAt: string;
    parent: Pick<Deck, 'originalTitle' | 'romajiTitle' | 'englishTitle' | 'coverName'> | null;
  };

  const MAX_ROWS = 3;

  const { $api } = useNuxtApp();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();
  const { followUpCompletion } = useCompletionFollowUp();
  const { postDeckStatus } = useMediaListApi();

  const decks = ref<StaleDeck[]>([]);
  const datePopover = ref();
  const closing = ref<{ deck: StaleDeck; status: DeckStatus.Completed | DeckStatus.Dropped } | null>(null);
  const busyDeckIds = ref(new Set<number>());

  async function whileBusy<T>(deckId: number, task: () => Promise<T>): Promise<T> {
    busyDeckIds.value = new Set(busyDeckIds.value).add(deckId);
    try {
      return await task();
    } finally {
      const next = new Set(busyDeckIds.value);
      next.delete(deckId);
      busyDeckIds.value = next;
    }
  }

  const progressed = new Set<number>();

  onMounted(async () => {
    try {
      decks.value = await $api<StaleDeck[]>('user/media-list/stale');
    } catch {
      decks.value = [];
    }
  });

  const visible = computed(() => decks.value.slice(0, MAX_ROWS));

  const heading = computed(() => {
    const verbs = new Set(visible.value.map((d) => ongoingVerb(d.mediaType)));
    return verbs.size === 1 ? `Still ${[...verbs][0]}?` : 'Still on these?';
  });

  const rowTitle = (deck: StaleDeck) => (deck.parent ? `${localiseTitle(deck.parent)} - ${localiseTitle(deck)}` : localiseTitle(deck));
  const rowCover = (deck: StaleDeck) => coverUrl(deck.parent?.coverName ?? deck.coverName);

  function rowFacts(deck: StaleDeck): string[] {
    const facts: string[] = [];
    if (deck.listEntry?.startedOn) facts.push(`Started ${formatReadDate(deck.listEntry.startedOn)}`);
    const percent = entryProgressPercent(deck.listEntry, deck.characterCount);
    if (percent != null) facts.push(`${percent}% done`);
    const units = deck.listEntry ? unitsFact(deck.listEntry, deck.mediaType) : null;
    if (units) facts.push(units);
    facts.push(`Last update ${formatRelativeTime(deck.lastUpdatedAt)}`);
    return facts;
  }

  function removeRow(deckId: number) {
    decks.value = decks.value.filter((d) => d.deckId !== deckId);
  }

  function confirmStillGoing(deck: StaleDeck) {
    return whileBusy(deck.deckId, async () => {
      try {
        await $api(`user/media-list/stale/${deck.deckId}/still-going`, { method: 'POST' });
        removeRow(deck.deckId);
      } catch (e) {
        toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not save that'), life: 5000 });
      }
    });
  }

  function pause(deck: StaleDeck) {
    return whileBusy(deck.deckId, () => postDeckStatus(deck, DeckStatus.Paused));
  }

  async function setStatus(deck: StaleDeck, status: DeckStatus, date: string | null) {
    const response = await whileBusy(deck.deckId, () => postDeckStatus(deck, status, { date }));
    if (response && status === DeckStatus.Completed) await followUpCompletion(deck, response, date);
  }

  const { menu: dateMenu, rememberTrigger, restoreFocus } = useMenuFocus();

  async function openDateChoice(deck: StaleDeck, status: DeckStatus.Completed | DeckStatus.Dropped, event: Event) {
    rememberTrigger(event);
    const target = event.currentTarget as HTMLElement;
    const sameRow = closing.value?.deck.deckId === deck.deckId && closing.value.status === status;
    if (sameRow) {
      datePopover.value?.toggle(event, target);
      return;
    }
    datePopover.value?.hide();
    closing.value = { deck, status };
    await nextTick();

    datePopover.value?.show({ currentTarget: target }, target);
  }

  function chooseDate(date: string | null) {
    datePopover.value?.hide();
    if (closing.value) setStatus(closing.value.deck, closing.value.status, date);
  }

  onMediaListChange((change) => {
    const index = decks.value.findIndex((d) => d.deckId === change.deckId);
    if (index === -1) return;
    if (change.status !== undefined && change.status !== DeckStatus.Ongoing) {
      removeRow(change.deckId);
      return;
    }
    decks.value[index] = applyMediaListChange(decks.value[index]!, change);
    if ('listEntry' in change) progressed.add(change.deckId);
  });

  function onProgressClosed(deckId: number) {
    if (progressed.has(deckId)) removeRow(deckId);
  }

  const historyDeck = useHistoryDialogDeck();
  watch(historyDeck, (current, previous) => {
    if (previous && !current) onProgressClosed(previous.deckId);
  });
</script>

<template>
  <section
    v-if="visible.length"
    class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm"
    aria-labelledby="home-stale-ongoing-heading"
  >
    <h2 id="home-stale-ongoing-heading" class="px-4 pt-3 text-sm font-semibold text-gray-800 dark:text-gray-100">{{ heading }}</h2>

    <ul class="divide-y divide-surface-100 dark:divide-surface-800">
      <li v-for="deck in visible" :key="deck.deckId" class="flex items-start gap-3 px-4 py-3">
        <NuxtLink :to="`/decks/media/${deck.deckId}/detail`" class="shrink-0" tabindex="-1" aria-hidden="true">
          <img :src="rowCover(deck)" alt="" class="h-14 w-10 rounded-xs object-cover" loading="lazy" />
        </NuxtLink>

        <div class="min-w-0 flex-1 flex flex-col gap-1.5">
          <div class="min-w-0">
            <NuxtLink
              :to="`/decks/media/${deck.deckId}/detail`"
              class="block truncate text-sm font-medium !text-inherit hover:underline"
              v-bind="japaneseTextAttrs(rowTitle(deck))"
            >
              {{ rowTitle(deck) }}
            </NuxtLink>
            <div class="flex flex-wrap gap-x-3 text-xs text-gray-600 dark:text-gray-300 tabular-nums">
              <span v-for="fact in rowFacts(deck)" :key="fact">{{ fact }}</span>
            </div>
          </div>

          <div class="flex flex-wrap items-center gap-1">
            <Button
              :label="`Still ${ongoingVerb(deck.mediaType)}`"
              size="small"
              :disabled="busyDeckIds.has(deck.deckId)"
              class="mr-1"
              @click="confirmStillGoing(deck)"
            />
            <MediaListProgressButton :deck="deck" hide-progress @close="onProgressClosed(deck.deckId)" />
            <Button
              label="Pause"
              icon="pi pi-pause"
              text
              size="small"
              severity="secondary"
              :disabled="busyDeckIds.has(deck.deckId)"
              @click="pause(deck)"
            />
            <Button
              label="Finished"
              icon="pi pi-check"
              text
              size="small"
              severity="success"
              :disabled="busyDeckIds.has(deck.deckId)"
              aria-haspopup="dialog"
              @click="openDateChoice(deck, DeckStatus.Completed, $event)"
            />
            <Button
              label="Dropped"
              icon="pi pi-times"
              text
              size="small"
              severity="secondary"
              :disabled="busyDeckIds.has(deck.deckId)"
              aria-haspopup="dialog"
              @click="openDateChoice(deck, DeckStatus.Dropped, $event)"
            />
          </div>
        </div>
      </li>
    </ul>

    <Popover ref="datePopover" :pt="{ content: { class: 'p-1' } }" @hide="restoreFocus">
      <div ref="dateMenu">
        <FinishDateChoice
          v-if="closing"
          :key="`${closing.deck.deckId}-${closing.status}`"
          :label="closing.status === DeckStatus.Completed ? 'When did you finish?' : 'When did you stop?'"
          :release-date="closing.deck.releaseDate"
          :start-date="closingPassStart(closing.deck.listEntry, closing.deck.status)"
          :hide-release="closing.status === DeckStatus.Dropped"
          back-label="Close"
          closes
          @choose="chooseDate"
          @back="datePopover?.hide()"
        />
      </div>
    </Popover>
  </section>
</template>
