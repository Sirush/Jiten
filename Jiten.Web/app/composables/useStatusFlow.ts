import { type Deck, type MediaListEntrySummary, DeckStatus, MediaListEntryState } from '~/types';
import type { SetStatusOptions } from '~/composables/useMediaListSync';
import { useConfirm } from 'primevue/useconfirm';
import { closingPassStart, dropQuestion, restartQuestion } from '~/utils/mediaListEntry';

export type StatusStep = 'list' | 'finished' | 'stopped' | 'reread' | 'resume' | 'unfinish' | 'progress';

export interface StatusFlowOptions {
  close: () => void;
  isOpen: () => boolean;
  home?: 'list' | 'progress';
}

export function useStatusFlow(deck: () => Deck, { close, isOpen, home = 'list' }: StatusFlowOptions) {
  const { postDeckStatus } = useMediaListApi();
  const { followUpCompletion } = useCompletionFollowUp();
  const confirm = useConfirm();

  const step = ref<StatusStep>(home);
  const undoCompletion = ref(false);
  const requestsInFlight = ref(0);
  const busy = computed(() => requestsInFlight.value > 0);

  async function send(target: Deck, status: DeckStatus, options?: SetStatusOptions) {
    requestsInFlight.value++;
    try {
      return await postDeckStatus(target, status, options);
    } finally {
      requestsInFlight.value--;
    }
  }
  const passStart = computed(() => closingPassStart(deck().listEntry, deck().status, { undoCompletion: undoCompletion.value }));

  function goTo(next: StatusStep) {
    if (next === home) undoCompletion.value = false;
    step.value = next;
  }

  const reset = () => goTo(home);

  function questionFor(status: DeckStatus, current: DeckStatus, entry: MediaListEntrySummary | null | undefined): StatusStep | null {
    switch (status) {
      case DeckStatus.Completed:
        return entry?.state === MediaListEntryState.Completed ? null : 'finished';
      case DeckStatus.Dropped:
        return dropQuestion(current, entry);
      case DeckStatus.Ongoing:
        return restartQuestion(current, entry);
      default:
        return null;
    }
  }

  function choose(status: DeckStatus) {
    if (busy.value) return;
    const target = deck();
    const current = target.status ?? DeckStatus.None;
    if (status === current) return close();

    const question = questionFor(status, current, target.listEntry);
    if (question) {
      goTo(question);
      return;
    }
    if (status === DeckStatus.Ongoing) {
      void start();
      return;
    }

    close();
    if (status === DeckStatus.None && (target.listEntry || [DeckStatus.Ongoing, DeckStatus.Completed, DeckStatus.Dropped].includes(current))) {
      confirm.require({
        message: 'Remove this title from your list? Its dates and character counts will be deleted too.',
        header: 'Remove from list',
        icon: 'pi pi-exclamation-triangle',
        acceptLabel: 'Remove',
        rejectLabel: 'Cancel',
        acceptProps: { severity: 'danger' },
        rejectProps: { severity: 'secondary' },
        accept: () => send(target, status),
      });
      return;
    }
    void send(target, status);
  }

  async function finish(date: string | null) {
    if (busy.value) return;
    const target = deck();
    close();
    const response = await send(target, DeckStatus.Completed, { date });
    await followUpCompletion(target, response, date);
  }

  async function stop(date: string | null) {
    if (busy.value) return;
    const target = deck();
    const undo = undoCompletion.value;
    close();
    await send(target, DeckStatus.Dropped, { date, undoCompletion: undo });
  }

  async function start(newEntry = false) {
    const target = deck();
    const response = await send(target, DeckStatus.Ongoing, { newEntry });
    if (!isOpen() || deck().deckId !== target.deckId) return;
    if (response) goTo('progress');
    else close();
  }

  function answer(choice: boolean) {
    if (busy.value) return;
    if (step.value !== 'unfinish') {
      void start(choice);
      return;
    }
    if (choice) {
      undoCompletion.value = true;
      step.value = 'stopped';
      return;
    }
    const target = deck();
    close();
    void send(target, DeckStatus.Dropped);
  }

  return { step: readonly(step), home, busy, passStart, goTo, reset, choose, finish, stop, start, answer, close };
}
