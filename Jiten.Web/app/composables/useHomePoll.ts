import type { Poll } from '~/types';

interface HomePollState {
  poll: Poll | null;
  nextPoll: Poll | null;
  allVoted: boolean;
  loading: boolean;
  failed: boolean;
  skipping: boolean;
  skipped: number[];
}

/**
 * The home page's poll, shared between the compact row and the full card so that both read one
 * fetch. `polls/home` returns an unvoted poll when one exists, else the latest voted one.
 */
export function useHomePoll() {
  const { $api } = useNuxtApp();
  const state = useState<HomePollState>('home-poll', () => ({
    poll: null,
    nextPoll: null,
    allVoted: false,
    loading: true,
    failed: false,
    skipping: false,
    skipped: [],
  }));
  const started = useState('home-poll-started', () => false);

  async function fetchHomePoll() {
    const query = state.value.skipped.length > 0 ? { exclude: state.value.skipped } : undefined;
    return (await $api<Poll | null>('polls/home', { query })) ?? null;
  }

  function applyFetched(fetched: Poll | null) {
    // Backstop for servers that ignore the exclude param
    if (fetched && fetched.myOptionIds.length === 0 && state.value.skipped.includes(fetched.id)) {
      state.value.allVoted = false;
      state.value.poll = null;
      return;
    }
    if (fetched && fetched.myOptionIds.length > 0) {
      state.value.allVoted = true;
      state.value.poll = null;
    } else {
      state.value.allVoted = false;
      state.value.poll = fetched;
    }
  }

  async function load() {
    if (started.value) return;
    started.value = true;
    state.value.skipped = readSkippedPollIds();
    try {
      applyFetched(await fetchHomePoll());
    } catch {
      state.value.failed = true;
    } finally {
      state.value.loading = false;
    }
  }

  async function onVoted(updated: Poll) {
    state.value.poll = updated;
    try {
      const fetched = await fetchHomePoll();
      state.value.nextPoll = fetched && fetched.id !== updated.id && fetched.myOptionIds.length === 0 ? fetched : null;
    } catch {
      state.value.nextPoll = null;
    }
  }

  function showNext() {
    if (!state.value.nextPoll) return;
    state.value.poll = state.value.nextPoll;
    state.value.nextPoll = null;
  }

  const canSkip = computed(() => state.value.poll !== null && state.value.poll.myOptionIds.length === 0);

  async function skip() {
    if (!state.value.poll || state.value.skipping) return;
    state.value.skipped = recordSkippedPollId(state.value.poll.id);
    try {
      state.value.skipping = true;
      applyFetched(await fetchHomePoll());
    } catch {
      state.value.poll = null;
    } finally {
      state.value.skipping = false;
    }
  }

  return { state, load, onVoted, showNext, skip, canSkip };
}
