<script setup lang="ts">
  import {
    FsrsRating,
    KnownState,
    MediaType,
    type Deck,
    type DeckDetail,
    type WatchCue,
    type WatchInfo,
    type WatchTimeline,
    type WatchWindow,
    type WatchWord,
    type Word,
  } from '~/types';
  import { useApiFetchPaginated } from '~/composables/useApiFetch';
  import { useAuthStore } from '~/stores/authStore';
  import { DEFAULT_WATCH_COLOURS, DEFAULT_WATCH_PREFS, useJitenStore, type WatchColourKey, type WatchPrefs } from '~/stores/jitenStore';
  import { useYouTubePlayer } from '~/composables/useYouTubePlayer';
  import { formatRuntime } from '~/utils/formatRuntime';
  import Popover from 'primevue/popover';
  import Select from 'primevue/select';
  import ToggleSwitch from 'primevue/toggleswitch';
  import InputNumber from 'primevue/inputnumber';
  import Button from 'primevue/button';
  import Skeleton from 'primevue/skeleton';
  import Message from 'primevue/message';
  import Tooltip from '~/components/Tooltip.vue';

  definePageMeta({
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const route = useRoute();
  const deckId = computed(() => Number(route.params.id));
  const localiseTitle = useLocaliseTitle();
  const authStore = useAuthStore();
  const { $api } = useNuxtApp();

  const { data: detail, ready: detailReady } = await useApiFetchPaginated<DeckDetail>(`media-deck/${deckId.value}/detail`, {
    revalidateOnClient: true,
    query: { offset: 0 },
  });

  if (import.meta.server) {
    await detailReady;
    const d = detail.value?.data?.mainDeck;
    if (!d || d.mediaType !== MediaType.YouTube) throw createError({ statusCode: 404, statusMessage: 'Not a video deck', fatal: true });
  }

  const mainDeck = computed<Deck | undefined>(() => detail.value?.data?.mainDeck);
  const parentDeck = computed<Deck | undefined | null>(() => detail.value?.data?.parentDeck);
  const title = computed(() => (mainDeck.value ? localiseTitle(mainDeck.value) : ''));

  useSeoMeta({
    title: () => (title.value ? `Watch ${title.value} with a Japanese transcript` : 'Watch'),
    description: () => `Watch ${title.value} with a synced Japanese transcript with your unknown words highlighted.`,
  });
  // Watch pages duplicate the child deck page for search; the deck page is the one to rank.
  useRobotsRule(() => 'noindex, follow');
  useHead(() => ({ link: [{ rel: 'canonical', href: `https://jiten.moe/decks/media/${deckId.value}/detail` }] }));

  const loggedIn = computed(() => authStore.isAuthenticated);

  // ---- video identity (no text) ----
  const info = ref<WatchInfo | null>(null);
  const infoStatus = ref<'pending' | 'success' | 'missing' | 'error'>('pending');

  const statusOf = (error: unknown) =>
    (error as { statusCode?: number; response?: { status?: number } } | null)?.statusCode ??
    (error as { response?: { status?: number } } | null)?.response?.status;

  const loadInfo = async () => {
    try {
      info.value = await $api<WatchInfo>(`watch/${deckId.value}`);
      infoStatus.value = 'success';
    } catch (error) {
      infoStatus.value = statusOf(error) === 404 ? 'missing' : 'error';
    }
  };

  // ---- player ----
  const playerHost = ref<HTMLElement | null>(null);
  const player = useYouTubePlayer(playerHost);
  const canEmbed = computed(() => !!info.value?.videoId && !player.embedBlocked.value);
  const watchOnYouTubeUrl = computed(() => (info.value?.videoId ? `https://www.youtube.com/watch?v=${info.value.videoId}` : '#'));
  const currentMs = computed(() => player.currentTime.value * 1000);

  // ---- windowed transcript: lines only ever arrive around the playback position, once the player runs ----
  const lines = ref(new Map<number, WatchCue>());
  const words = ref<Record<string, WatchWord>>({});
  const conjugations = ref<string[][]>([]);
  const cueCount = ref(0);
  let loadingWindow: Promise<void> | null = null;

  const clearText = () => {
    lines.value = new Map();
    words.value = {};
  };

  // The poll asks every 200ms; a failed fetch waits before the next try so a bad minute cannot exhaust the rate limit
  let windowRetryAt = 0;
  let windowFailures = 0;
  const loadWindow = async (query: { at?: number; index?: number }) => {
    if (!player.ready.value || player.embedBlocked.value) return;
    if (loadingWindow) return loadingWindow;
    if (Date.now() < windowRetryAt) return;
    loadingWindow = (async () => {
      try {
        // A request that never answers must not wedge the loader for the rest of the video
        const window = await $api<WatchWindow>(`watch/${deckId.value}/window`, { query, signal: AbortSignal.timeout(10_000) });
        windowFailures = 0;
        const next = new Map(lines.value);
        for (const line of window.lines) next.set(line.index, line);
        // Lines far behind playback are dropped, so the page never accumulates the transcript
        const keep = Math.min(...window.lines.map((l) => l.index)) - 20;
        for (const key of next.keys()) if (key < keep) next.delete(key);
        lines.value = next;
        words.value = { ...words.value, ...window.words };
        conjugations.value = window.conjugations ?? [];
        cueCount.value = window.cueCount;
      } catch (error) {
        if (statusOf(error) === 404) clearText();
        windowFailures++;
        windowRetryAt = Date.now() + Math.min(10_000, 1_000 * 2 ** windowFailures);
      } finally {
        loadingWindow = null;
      }
    })();
    return loadingWindow;
  };

  const sortedIndexes = computed(() => [...lines.value.keys()].sort((a, b) => a - b));

  const activeIndex = computed(() => {
    const t = currentMs.value;
    let best = -1;
    for (const index of sortedIndexes.value) {
      const cue = lines.value.get(index)!;
      if (cue.start <= t) best = index;
      else break;
    }
    if (best < 0) return -1;
    const cue = lines.value.get(best)!;
    return t > cue.end + 1500 && best !== cueCount.value - 1 ? -1 : best;
  });

  // Keeps the focus when playback sits in a gap between lines.
  const lastActive = ref(-1);
  watch(activeIndex, (i) => {
    if (i >= 0) lastActive.value = i;
  });

  const loadedMax = computed(() => (sortedIndexes.value.length ? sortedIndexes.value[sortedIndexes.value.length - 1]! : -1));
  const loadedMin = computed(() => (sortedIndexes.value.length ? sortedIndexes.value[0]! : -1));

  // Fetch the next window when playback nears the loaded edge, or lands somewhere unloaded after a seek
  watch(currentMs, (t) => {
    if (!player.ready.value || player.embedBlocked.value) return;
    if (lines.value.size === 0) {
      loadWindow({ at: Math.floor(t) });
      return;
    }
    const last = lines.value.get(loadedMax.value)!;
    const first = lines.value.get(loadedMin.value)!;
    // Beyond the transcript's own ends there is nothing more to fetch
    const pastEnd = t > last.end + 1500 && loadedMax.value < cueCount.value - 1;
    const beforeStart = t < first.start - 1500 && loadedMin.value > 0;
    const outside = pastEnd || beforeStart;
    const nearEnd = activeIndex.value >= 0 && loadedMax.value - activeIndex.value <= 3 && loadedMax.value < cueCount.value - 1;
    // Seeking back inside the loaded window leaves nothing above it, so the window also refills backwards
    const nearStart = activeIndex.value >= 0 && activeIndex.value - loadedMin.value <= 3 && loadedMin.value > 0;
    if (outside || nearEnd || nearStart) loadWindow({ at: Math.floor(t) });
  });

  const rateOptions = [0.5, 0.75, 1, 1.25, 1.5, 2].map((v) => ({ label: `${v}x`, value: v }));
  const jitenStore = useJitenStore();
  // Stored prefs may predate a field, so defaults fill the gaps
  const prefs = computed<WatchPrefs>(() => ({
    ...DEFAULT_WATCH_PREFS,
    ...jitenStore.watchPrefs,
    colours: { ...DEFAULT_WATCH_COLOURS, ...(jitenStore.watchPrefs?.colours ?? {}) },
  }));
  const setPref = <K extends keyof WatchPrefs>(key: K, value: WatchPrefs[K]) => {
    jitenStore.watchPrefs = { ...prefs.value, [key]: value };
  };
  const autoPause = computed({ get: () => prefs.value.autoPause, set: (v: boolean) => setPref('autoPause', v) });
  const pauseOffsetMs = computed({
    get: () => prefs.value.pauseOffsetMs,
    set: (v: number) => setPref('pauseOffsetMs', v ?? DEFAULT_WATCH_PREFS.pauseOffsetMs),
  });
  const blurKnown = computed({ get: () => prefs.value.blurKnown, set: (v: boolean) => setPref('blurKnown', v) });
  const pauseOnLookup = computed({ get: () => prefs.value.pauseOnLookup, set: (v: boolean) => setPref('pauseOnLookup', v) });
  const sentenceContext = computed({ get: () => prefs.value.sentenceContext, set: (v: number) => setPref('sentenceContext', v) });
  // Session-only: dims everything but the player, controls and transcript
  const lightsOff = ref(false);
  const pausedAt = ref(-1);

  let pauseTimer: ReturnType<typeof setTimeout> | null = null;

  const disarmPause = () => {
    if (pauseTimer) clearTimeout(pauseTimer);
    pauseTimer = null;
  };

  // A timer lands the pause within a few ms of the target; the 200ms poll alone would overshoot into the next line.
  const armPause = () => {
    disarmPause();
    const i = activeIndex.value;
    if (!autoPause.value || !player.playing.value || i < 0 || i === pausedAt.value) return;
    const target = lines.value.get(i)!.end + pauseOffsetMs.value;
    const delay = (target - currentMs.value) / player.playbackRate.value;
    pauseTimer = setTimeout(
      () => {
        pauseTimer = null;
        pausedAt.value = i;
        player.pause();
      },
      Math.max(0, delay)
    );
  };
  // Re-arming on every poll tick corrects timer drift against the player clock
  watch([activeIndex, autoPause, pauseOffsetMs, currentMs, () => player.playing.value, () => player.playbackRate.value], armPause);
  onBeforeUnmount(disarmPause);
  watch(
    () => player.playing.value,
    (isPlaying) => {
      if (isPlaying && activeIndex.value !== pausedAt.value) pausedAt.value = -1;
    }
  );

  const seekToIndex = async (index: number) => {
    if (index < 0 || (cueCount.value > 0 && index >= cueCount.value)) return;
    if (!lines.value.has(index)) await loadWindow({ index });
    const cue = lines.value.get(index);
    if (!cue) return;
    pausedAt.value = -1;
    player.seek(cue.start / 1000);
  };
  const focusIndex = computed(() => (activeIndex.value >= 0 ? activeIndex.value : Math.max(0, lastActive.value)));
  const replayLine = () => seekToIndex(focusIndex.value);
  const stepLine = (delta: number) => seekToIndex(focusIndex.value + delta);

  // ---- karaoke window: the current line, faded neighbours, the outermost invisible so lines fade in and out ----
  const KARAOKE_WINDOW = 3;
  const karaokeLines = computed(() => {
    const centre = focusIndex.value;
    const out: { index: number; offset: number; cue: WatchCue }[] = [];
    for (let offset = -KARAOKE_WINDOW; offset <= KARAOKE_WINDOW; offset++) {
      const cue = lines.value.get(centre + offset);
      if (cue) out.push({ index: centre + offset, offset, cue });
    }
    return out;
  });
  // A seek past the window shares no lines with the old one, so a crossfade only stacks two transcripts.
  const cutTransition = ref(false);
  let cutTimer: ReturnType<typeof setTimeout> | undefined;
  watch(
    focusIndex,
    (next, prev) => {
      if (Math.abs(next - prev) <= KARAOKE_WINDOW) return;
      cutTransition.value = true;
      clearTimeout(cutTimer);
      cutTimer = setTimeout(() => (cutTransition.value = false), 50);
    },
    { flush: 'sync' }
  );
  onBeforeUnmount(() => clearTimeout(cutTimer));
  // A leaving line is pulled out of the flow, so it has to be pinned where it stood or it fades out at the centre
  const pinLeavingLine = (el: Element) => {
    const line = el as HTMLElement;
    line.style.top = `${line.offsetTop}px`;
    line.style.left = `${line.offsetLeft}px`;
    line.style.width = `${line.offsetWidth}px`;
  };
  const karaokeLineStyle = (offset: number) => {
    const distance = Math.abs(offset);
    const size = ['clamp(1.25rem, 2.5vw, 1.5rem)', '1.125rem', '1rem', '1rem'][distance] ?? '1rem';
    const opacity = [1, 0.75, 0.4, 0][distance] ?? 0;
    return { fontSize: size, opacity, fontWeight: distance === 0 ? 600 : 400, gridRow: offset + KARAOKE_WINDOW + 1 };
  };

  // ---- word colours ----
  const colourRows: { key: WatchColourKey; label: string }[] = [
    { key: 'new', label: 'Unknown' },
    { key: 'young', label: 'Young' },
    { key: 'due', label: 'Due' },
    { key: 'mature', label: 'Mature / Mastered' },
    { key: 'redundant', label: 'Redundant' },
    { key: 'ignored', label: 'Blacklisted / Suspended' },
  ];
  const colourKeyOf = (states: KnownState[] | undefined): WatchColourKey => {
    if (!states || states.length === 0) return 'new';
    if (states.includes(KnownState.Blacklisted) || states.includes(KnownState.Suspended)) return 'ignored';
    if (states.includes(KnownState.Redundant)) return 'redundant';
    if (states.includes(KnownState.Mastered) || states.includes(KnownState.Mature)) return 'mature';
    if (states.includes(KnownState.Due)) return 'due';
    if (states.includes(KnownState.Young)) return 'young';
    return 'new';
  };
  const isKnown = (states: KnownState[] | undefined) => colourKeyOf(states) === 'mature';
  const wordStyle = (word: WatchWord) => {
    const colour = prefs.value.colours[colourKeyOf(word.knownStates)];
    return colour ? { color: colour } : undefined;
  };
  const setColour = (key: WatchColourKey, value: string | null) => setPref('colours', { ...prefs.value.colours, [key]: value });
  const resetColours = () => setPref('colours', { ...DEFAULT_WATCH_COLOURS });
  const coloursOp = ref();
  const toggleColours = (event: Event) => coloursOp.value?.toggle(event);
  // Native colour inputs need a concrete value; unset rows show the theme text colour
  const colourInputValue = (key: WatchColourKey) => prefs.value.colours[key] ?? (isDark.value ? '#f3f4f6' : '#111827');
  const isDark = ref(false);
  onMounted(() => {
    isDark.value = document.documentElement.classList.contains('dark-mode');
  });

  type Segment = { text: string; start: number; word?: WatchWord; conjugation?: string[] };
  const segmentsOf = (cue: WatchCue): Segment[] => {
    const out: Segment[] = [];
    let at = 0;
    for (const [wordId, readingIndex, start, length, conjugation] of cue.tokens) {
      if (start > at) out.push({ text: cue.text.slice(at, start), start: at });
      out.push({
        text: cue.text.slice(start, start + length),
        start,
        word: words.value[`${wordId}-${readingIndex}`],
        conjugation: conjugation !== undefined && conjugation >= 0 ? conjugations.value[conjugation] : undefined,
      });
      at = start + length;
    }
    if (at < cue.text.length) out.push({ text: cue.text.slice(at), start: at });
    return out;
  };

  // A drag that ends on a word is a selection, not a lookup
  const onWordClick = (line: { index: number; offset: number }, segment: Segment) => {
    if (window.getSelection()?.toString()) return;
    if (line.offset !== 0) seekToIndex(line.index);
    else if (segment.word) openWord(segment.word, segment.conjugation, { index: line.index, start: segment.start, length: segment.text.length });
  };

  // ---- unknown-word timeline (counts only) ----
  const timeline = ref<WatchTimeline | null>(null);
  const loadTimeline = async () => {
    try {
      timeline.value = await $api<WatchTimeline>(`watch/${deckId.value}/timeline`, { query: { buckets: 60 } });
    } catch {
      timeline.value = null;
    }
  };
  const timelineMax = computed(() => (timeline.value ? Math.max(0, ...timeline.value.counts) : 0));
  const progressPercent = computed(() => (timeline.value && timeline.value.totalMs > 0 ? Math.min(100, (currentMs.value / timeline.value.totalMs) * 100) : 0));
  const bucketTimeMs = (bucket: number) => {
    const t = timeline.value!;
    const start = t.starts?.[bucket] ?? -1;
    return start >= 0 ? start : (bucket / t.counts.length) * t.totalMs;
  };
  const seekToBucket = (bucket: number) => {
    if (!timeline.value || timeline.value.totalMs <= 0) return;
    pausedAt.value = -1;
    player.seek(bucketTimeMs(bucket) / 1000);
  };

  // ---- word panel ----
  const panelWord = ref<Word | null>(null);
  const panelConjugation = ref<string[]>([]);
  // Where the clicked surface form sits, so the mined sentence can mark it and pull neighbouring lines
  const panelOrigin = ref<{ index: number; start: number; length: number } | null>(null);
  const panelLoading = ref(false);
  const panelOpen = computed(() => panelLoading.value || panelWord.value !== null);
  const grading = ref(false);
  const toast = useToast();
  // One grade per word per minute; the row gives way to a receipt so a double tap cannot grade twice
  const GRADE_COOLDOWN_MS = 60_000;
  const recentGrades = ref(new Map<string, { rating: FsrsRating; at: number }>());
  const wordKey = (word: Word) => `${word.wordId}-${word.mainReading.readingIndex}`;
  const lastGrade = computed(() => {
    const word = panelWord.value;
    if (!word) return null;
    const recent = recentGrades.value.get(wordKey(word));
    return recent && Date.now() - recent.at < GRADE_COOLDOWN_MS ? recent.rating : null;
  });

  // The word payload is client-cached for five minutes, which would hide a grade the user just gave
  const fetchWord = (wordId: number, readingIndex: number) => $api<Word>(`vocabulary/${wordId}/${readingIndex}`, { query: { t: Date.now() } });
  const openWord = async (word: WatchWord | undefined, conjugation: string[] = [], origin: { index: number; start: number; length: number } | null = null) => {
    if (!word) return;
    if (pauseOnLookup.value && player.playing.value) player.pause();
    panelLoading.value = true;
    panelWord.value = null;
    panelConjugation.value = conjugation;
    panelOrigin.value = origin;
    try {
      panelWord.value = await fetchWord(word.wordId, word.readingIndex);
    } finally {
      panelLoading.value = false;
    }
  };
  const closeWord = () => {
    panelWord.value = null;
  };
  // A status change in the panel changes colours everywhere the word appears.
  const refreshStates = async () => {
    const word = panelWord.value;
    const reload = loadWindow({ at: Math.floor(currentMs.value) }).then(() => loadTimeline());
    if (word) {
      const fresh = await fetchWord(word.wordId, word.mainReading.readingIndex);
      if (panelWord.value === word) panelWord.value = fresh;
    }
    await reload;
  };
  const SENTENCE_MAX = 150;
  // The current line with the word marked, plus up to `radius` loaded lines on each side; trimmed around the word past the limit
  const buildSentence = (radius: number): string => {
    const origin = panelOrigin.value;
    if (!origin) return '';
    const cue = lines.value.get(origin.index);
    if (!cue) return '';
    const marked = `${cue.text.slice(0, origin.start)}**${cue.text.slice(origin.start, origin.start + origin.length)}**${cue.text.slice(origin.start + origin.length)}`;
    const before: string[] = [];
    const after: string[] = [];
    for (let i = 1; i <= radius; i++) {
      const prev = lines.value.get(origin.index - i);
      const next = lines.value.get(origin.index + i);
      if (prev) before.unshift(prev.text);
      if (next) after.push(next.text);
    }
    const text = [...before, marked, ...after].join(' ');
    if (text.length <= SENTENCE_MAX) return text;
    const wordAt = text.indexOf('**');
    const wordEnd = text.indexOf('**', wordAt + 2) + 2;
    let from = wordAt;
    let to = wordEnd;
    while (to - from < SENTENCE_MAX && (from > 0 || to < text.length)) {
      if (from > 0) from--;
      if (to - from < SENTENCE_MAX && to < text.length) to++;
    }
    return text.slice(from, to);
  };
  const minedSentence = computed(() => buildSentence(sentenceContext.value));
  const canExpandSentence = computed(() => {
    const origin = panelOrigin.value;
    if (!origin) return false;
    const r = sentenceContext.value + 1;
    return lines.value.has(origin.index - r) || lines.value.has(origin.index + r);
  });
  const minedFor = ref(new Set<string>());
  const sentenceMined = computed(() => !!panelWord.value && minedFor.value.has(wordKey(panelWord.value)));
  const mining = ref(false);
  const mineSentence = async () => {
    const word = panelWord.value;
    const origin = panelOrigin.value;
    const text = minedSentence.value;
    if (!word || !origin || !text || mining.value) return;
    const cue = lines.value.get(origin.index);
    mining.value = true;
    try {
      await $api(`user/example-sentences/${word.wordId}/${word.mainReading.readingIndex}/favourite`, {
        method: 'POST',
        body: { text, source: cue ? `${title.value} ${formatTime(cue.start)}` : title.value },
      });
      minedFor.value = new Set(minedFor.value).add(wordKey(word));
    } catch {
      toast.add({ severity: 'error', summary: 'Sentence not saved', detail: 'You may have reached the custom sentence limit for this word.', life: 5000 });
    } finally {
      mining.value = false;
    }
  };

  const gradeWord = async (rating: FsrsRating) => {
    const word = panelWord.value;
    if (!word || grading.value || lastGrade.value !== null) return;
    grading.value = true;
    try {
      await $api('srs/review', {
        method: 'POST',
        body: { wordId: word.wordId, readingIndex: word.mainReading.readingIndex, rating, clientRequestId: crypto.randomUUID() },
      });
      recentGrades.value = new Map(recentGrades.value).set(wordKey(word), { rating, at: Date.now() });
      await refreshStates();
    } catch {
      toast.add({ severity: 'error', summary: 'Review not saved', detail: 'Try again in a moment.', life: 4000 });
    } finally {
      grading.value = false;
    }
  };

  const formatTime = (ms: number) => {
    const s = Math.floor(ms / 1000);
    const m = Math.floor(s / 60);
    const h = Math.floor(m / 60);
    const mm = h > 0 ? String(m % 60).padStart(2, '0') : String(m);
    return `${h > 0 ? `${h}:` : ''}${mm}:${String(s % 60).padStart(2, '0')}`;
  };

  const gradeKeys: Record<string, FsrsRating> = { '1': FsrsRating.Again, '2': FsrsRating.Hard, '3': FsrsRating.Good, '4': FsrsRating.Easy };
  const onKey = (event: KeyboardEvent) => {
    const target = event.target as HTMLElement | null;
    if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return;
    if (event.key === 'Escape' && panelOpen.value) closeWord();
    else if (gradeKeys[event.key] !== undefined && panelWord.value) gradeWord(gradeKeys[event.key]!);
    else if (event.key === 'r') replayLine();
    else if (event.key === 'l') lightsOff.value = !lightsOff.value;
    else if (event.key === 'j') stepLine(1);
    else if (event.key === 'k') stepLine(-1);
    else if (event.key === ' ' && canEmbed.value) {
      event.preventDefault();
      if (player.playing.value) player.pause();
      else player.play();
    }
  };

  // Text is requested only once the player has proven the video plays here
  watch(
    () => player.ready.value,
    (ready) => {
      if (!ready || player.embedBlocked.value) return;
      loadWindow({ at: 0 });
      loadTimeline();
    }
  );
  watch(
    () => player.embedBlocked.value,
    (blocked) => {
      if (!blocked) return;
      player.destroy();
      clearText();
      timeline.value = null;
    }
  );

  onMounted(async () => {
    window.addEventListener('keydown', onKey);
    if (!loggedIn.value) return;
    await loadInfo();
    if (info.value?.videoId) await player.mount(info.value.videoId);
  });
  onBeforeUnmount(() => window.removeEventListener('keydown', onKey));
</script>

<template>
  <div class="max-w-4xl mx-auto">
    <DeckBreadcrumb :deck="mainDeck" :parent-deck="parentDeck" current="Watch" deck-label="Video" class="mb-3" />

    <div v-if="mainDeck" class="flex flex-col gap-2 mb-4">
      <h1 class="text-xl sm:text-2xl font-bold leading-snug" lang="ja">{{ title }}</h1>
      <div class="flex flex-wrap items-center gap-x-3 gap-y-2 text-sm text-surface-500 dark:text-surface-400">
        <span v-if="mainDeck.runtimeSeconds" class="tabular-nums">{{ formatRuntime(mainDeck.runtimeSeconds) }}</span>
        <span v-if="timeline">
          <span class="font-semibold text-rose-600 dark:text-rose-400 tabular-nums">{{ timeline.unknownWords }}</span> unknown words
        </span>
        <Tooltip v-if="mainDeck.coverage > 0" content="Share of this video's words you already know">
          <span class="tabular-nums"><span class="font-semibold text-emerald-600 dark:text-emerald-400">{{ mainDeck.coverage.toFixed(1) }}%</span> coverage</span>
        </Tooltip>
        <NuxtLink
          :to="`/decks/media/${deckId}/vocabulary`"
          class="inline-flex items-center gap-2 rounded border border-surface-200 dark:border-surface-700 px-2.5 py-1 text-primary-500 hover:bg-surface-100 dark:hover:bg-surface-800"
        >
          <i class="pi pi-book text-xs" aria-hidden="true" /> Vocabulary
        </NuxtLink>
      </div>
    </div>

    <Message v-if="!loggedIn" severity="info" :closable="false" class="mb-4">
      <NuxtLink to="/login" class="font-semibold">Log in</NuxtLink> to watch with the synced transcript and your word colours.
    </Message>
    <Message v-else-if="infoStatus === 'missing'" severity="warn" :closable="false" class="mb-4">
      This video has no timed transcript, or is no longer available on YouTube.
    </Message>
    <Message v-else-if="infoStatus === 'error'" severity="error" :closable="false" class="mb-4">The transcript could not be loaded.</Message>

    <Teleport to="body">
      <Transition name="fade">
        <div v-if="lightsOff" class="fixed inset-0 z-30 bg-black/95 cursor-pointer" aria-hidden="true" @click="lightsOff = false" />
      </Transition>
    </Teleport>

    <div v-if="loggedIn && infoStatus === 'success'" class="flex flex-col gap-3" :class="lightsOff ? 'relative z-[35] -m-3 p-3 rounded-xl bg-surface-0 dark:bg-surface-950' : ''">
      <div class="aspect-video w-full rounded-lg overflow-hidden bg-surface-900 [&>iframe]:w-full [&>iframe]:h-full">
        <div v-if="canEmbed" ref="playerHost" class="w-full h-full" />
        <div v-else class="w-full h-full flex flex-col items-center justify-center gap-3 text-surface-100 p-6 text-center">
          <p>The channel has disabled embeds, please watch it directly on YouTube instead.</p>
          <a
            :href="watchOnYouTubeUrl"
            target="_blank"
            rel="noopener"
            class="inline-flex items-center gap-2 rounded bg-surface-0 text-surface-900 px-3 py-1.5 text-sm font-medium"
          >
            <i class="pi pi-youtube" /> Watch on YouTube
          </a>
        </div>
      </div>

      <div v-if="canEmbed" class="flex flex-wrap items-center gap-x-4 gap-y-3 border-b border-surface-200 dark:border-surface-700 pb-3 text-sm">
        <div role="group" aria-label="Playback controls" class="flex items-center gap-3">
          <div class="flex items-center gap-1">
            <Tooltip content="Previous line (k)">
              <Button
                icon="pi pi-step-backward"
                size="small"
                severity="secondary"
                text
                rounded
                aria-label="Previous line"
                :disabled="!player.ready.value"
                @click="stepLine(-1)"
              />
            </Tooltip>
            <Tooltip content="Replay this line (r)">
              <Button
                icon="pi pi-replay"
                size="small"
                severity="secondary"
                rounded
                aria-label="Replay line"
                :disabled="!player.ready.value"
                @click="replayLine"
              />
            </Tooltip>
            <Tooltip content="Next line (j)">
              <Button
                icon="pi pi-step-forward"
                size="small"
                severity="secondary"
                text
                rounded
                aria-label="Next line"
                :disabled="!player.ready.value"
                @click="stepLine(1)"
              />
            </Tooltip>
          </div>
          <Select
            :model-value="player.playbackRate.value"
            :options="rateOptions"
            option-label="label"
            option-value="value"
            size="small"
            class="w-24"
            aria-label="Playback speed"
            @update:model-value="player.setRate"
          />
        </div>
        <div role="group" aria-label="Learning controls" class="watch-learning-controls flex flex-wrap items-center gap-x-4 gap-y-3">
          <div class="flex items-center gap-2 whitespace-nowrap">
            <ToggleSwitch v-model="autoPause" input-id="autoPause" />
            <label for="autoPause">Pause after each line</label>
          </div>
          <div class="flex items-center gap-2 whitespace-nowrap">
            <ToggleSwitch v-model="blurKnown" input-id="blurKnown" />
            <label for="blurKnown">Blur known words</label>
          </div>
          <div class="flex items-center gap-2 whitespace-nowrap">
            <ToggleSwitch v-model="pauseOnLookup" input-id="pauseOnLookup" />
            <label for="pauseOnLookup">Pause on lookup</label>
          </div>
          <Tooltip content="Customise vocabulary colours">
            <Button icon="pi pi-palette" text rounded size="small" severity="secondary" aria-label="Customise vocabulary colours" @click="toggleColours" />
          </Tooltip>
          <Tooltip :content="lightsOff ? 'Lights on (l)' : 'Lights off (l)'">
            <Button
              icon="pi pi-lightbulb"
              text
              rounded
              size="small"
              severity="secondary"
              :class="lightsOff ? '!text-amber-500' : ''"
              :aria-label="lightsOff ? 'Lights on' : 'Lights off'"
              @click="lightsOff = !lightsOff"
            />
          </Tooltip>
        </div>
        <Popover ref="coloursOp" :pt="{ content: { class: 'p-2' } }">
          <div class="flex flex-col gap-1.5 text-sm">
            <label v-for="row in colourRows" :key="row.key" class="flex items-center justify-between gap-4">
              <span class="flex items-center gap-2">
                <span class="font-noto-sans text-base" lang="ja" :style="prefs.colours[row.key] ? { color: prefs.colours[row.key]! } : undefined">言葉</span>
                {{ row.label }}
              </span>
              <span class="flex items-center gap-1">
                <input
                  type="color"
                  class="h-7 w-9 cursor-pointer rounded border border-surface-300 dark:border-surface-600 bg-transparent"
                  :value="colourInputValue(row.key)"
                  @input="setColour(row.key, ($event.target as HTMLInputElement).value)"
                />
                <Button
                  icon="pi pi-undo"
                  text
                  rounded
                  size="small"
                  severity="secondary"
                  aria-label="Reset colour"
                  :class="prefs.colours[row.key] === DEFAULT_WATCH_COLOURS[row.key] ? 'invisible' : ''"
                  @click="setColour(row.key, DEFAULT_WATCH_COLOURS[row.key])"
                />
              </span>
            </label>
            <Button label="Reset all" text size="small" severity="secondary" class="self-end" @click="resetColours" />
          </div>
        </Popover>
        <div v-if="autoPause" class="flex w-full items-center gap-2">
          <label for="pauseOffset" class="text-surface-500 dark:text-surface-400">Offset</label>
          <InputNumber
            v-model="pauseOffsetMs"
            input-id="pauseOffset"
            show-buttons
            :min="-1000"
            :max="1000"
            :step="25"
            suffix=" ms"
            size="small"
            :input-style="{ width: '8.5rem' }"
            aria-label="Pause offset in milliseconds"
          />
        </div>
      </div>

      <div v-if="canEmbed && (timeline || cueCount)" class="flex flex-col gap-2">
        <div class="flex items-center justify-between gap-3 text-xs text-surface-500 dark:text-surface-400">
          <span v-if="timeline && timelineMax > 0" class="font-medium">Vocabulary timeline</span>
          <span v-else class="font-medium">Transcript</span>
          <span v-if="cueCount && focusIndex >= 0" class="tabular-nums">Line {{ focusIndex + 1 }} / {{ cueCount }}</span>
        </div>
        <template v-if="timeline && timelineMax > 0">
          <div class="relative">
            <div class="flex h-4 gap-px rounded overflow-hidden bg-surface-100 dark:bg-surface-800">
              <Tooltip
                v-for="(count, i) in timeline.counts"
                :key="i"
                :content="count > 0 ? `${count} unknown ${count === 1 ? 'word' : 'words'} at ${formatTime(bucketTimeMs(i))}` : ''"
              >
                <button
                  type="button"
                  class="timeline-bucket flex-1 min-w-0 cursor-pointer hover:outline hover:outline-1 hover:outline-rose-700 focus-visible:outline focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary-700"
                  :style="{ '--bucket-intensity': `${count === 0 ? 0 : 15 + 65 * (count / timelineMax)}%` }"
                  :aria-label="`Seek to ${formatTime(bucketTimeMs(i))}`"
                  @click="seekToBucket(i)"
                />
              </Tooltip>
            </div>
            <div
              class="absolute -inset-y-1 w-0.5 -translate-x-1/2 rounded bg-primary-600 dark:bg-primary-400 ring-1 ring-surface-0 dark:ring-surface-900 pointer-events-none"
              :style="{ left: `${progressPercent}%` }"
              aria-hidden="true"
            >
              <span class="absolute -top-0.5 left-1/2 h-1.5 w-1.5 -translate-x-1/2 rounded-full bg-inherit" />
            </div>
          </div>
          <div class="text-xs text-surface-500 dark:text-surface-400">Darker sections have more unknown words. Click to jump.</div>
        </template>
      </div>

      <Skeleton v-if="canEmbed && !player.ready.value" height="14rem" />
      <div v-else-if="canEmbed" class="relative">
        <TransitionGroup
          tag="div"
          :name="cutTransition ? 'karaoke-cut' : 'karaoke'"
          class="karaoke-window relative grid items-center justify-items-center text-center overflow-hidden select-text"
          :class="{ 'karaoke-cut': cutTransition }"
          @before-leave="pinLeavingLine"
        >
          <p
            v-for="line in karaokeLines"
            :key="line.index"
            class="karaoke-line w-full max-w-3xl leading-relaxed font-noto-sans"
            :class="{ 'karaoke-context': line.offset !== 0, 'invisible h-0 overflow-hidden': Math.abs(line.offset) === KARAOKE_WINDOW }"
            :style="karaokeLineStyle(line.offset)"
            :aria-hidden="Math.abs(line.offset) === KARAOKE_WINDOW ? true : undefined"
            lang="ja"
          >
            <template v-for="(segment, j) in segmentsOf(line.cue)" :key="j">
              <Tooltip v-if="segment.word" :content="segment.word.reading !== segment.word.spelling ? segment.word.reading : ''">
                <span
                  role="button"
                  tabindex="0"
                  class="cursor-pointer rounded-sm"
                  :class="{ 'known-blur': blurKnown && isKnown(segment.word.knownStates), 'hover:bg-surface-200 dark:hover:bg-surface-700': line.offset === 0 }"
                  :style="wordStyle(segment.word)"
                  @click="onWordClick(line, segment)"
                  @keydown.enter.prevent="onWordClick(line, segment)"
                  @keydown.space.prevent="onWordClick(line, segment)"
                >
                  {{ segment.text }}
                </span>
              </Tooltip>
              <span v-else :class="line.offset !== 0 ? 'cursor-pointer' : ''" @click="onWordClick(line, segment)">{{ segment.text }}</span>
            </template>
          </p>
          <p v-if="karaokeLines.length === 0" key="empty" class="row-start-4 text-surface-500 dark:text-surface-400">Press play to follow the transcript.</p>
        </TransitionGroup>
        <WatchWordPanel
          v-if="panelOpen"
          :word="panelWord"
          :conjugation="panelConjugation"
          :loading="panelLoading"
          :grading="grading"
          :last-grade="lastGrade"
          :sentence="minedSentence"
          :sentence-context="sentenceContext"
          :can-expand-sentence="canExpandSentence"
          :sentence-mined="sentenceMined"
          :mining="mining"
          @close="closeWord"
          @grade="gradeWord"
          @changed="refreshStates"
          @mine="mineSentence"
          @update:sentence-context="sentenceContext = $event"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
  .fade-enter-active,
  .fade-leave-active {
    transition: opacity 0.3s ease;
  }
  .fade-enter-from,
  .fade-leave-to {
    opacity: 0;
  }

  .watch-learning-controls {
    flex: 1 1 34rem;
  }

  @media (min-width: 640px) {
    .watch-learning-controls {
      border-left: 1px solid var(--p-content-border-color);
      padding-left: 1rem;
    }
  }

  .karaoke-window {
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: 0 minmax(2.75rem, auto) minmax(2.75rem, auto) minmax(4rem, auto) minmax(2.75rem, auto) minmax(2.75rem, auto) 0;
    padding: 0.5rem 0.75rem;
  }

  .karaoke-context {
    filter: saturate(0.65);
  }

  .timeline-bucket {
    background-color: color-mix(in srgb, var(--p-rose-400) var(--bucket-intensity), transparent);
  }

  .known-blur {
    filter: blur(5px);
    transition: filter 0.15s ease;
  }
  .known-blur:hover,
  .known-blur:focus-visible {
    filter: none;
  }

  .karaoke-line {
    grid-column: 1;
    transition:
      font-size 0.5s ease,
      opacity 0.5s ease,
      transform 0.5s ease;
  }

  .karaoke-move,
  .karaoke-enter-active,
  .karaoke-leave-active {
    transition:
      transform 0.5s ease,
      opacity 0.5s ease;
  }

  .karaoke-enter-from,
  .karaoke-leave-to {
    opacity: 0;
  }

  .karaoke-leave-active {
    position: absolute;
    grid-area: auto !important;
  }

  .karaoke-cut .karaoke-line,
  .karaoke-cut-move,
  .karaoke-cut-enter-active,
  .karaoke-cut-leave-active {
    transition: none;
  }

  @media (prefers-reduced-motion: reduce) {
    .karaoke-line,
    .karaoke-move,
    .karaoke-enter-active,
    .karaoke-leave-active {
      transition: none;
    }
  }
</style>
