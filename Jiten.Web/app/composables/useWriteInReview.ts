import { useSrsStore } from '~/stores/srsStore';
import { FsrsRating } from '~/types';
import type { StudyCardDto } from '~/types';
import { checkMeaning, checkReading, hasKanji, playWriteInChime, shuffleInPlace, type WriteInMode, type WriteInResult } from '~/utils/srsWriteIn';

/**
 * Write-in review session logic: per-card modality assignment (equal-split shuffled bag), the
 * input → reveal phase, answer checking, the suggested grade, and the optional auto-advance.
 * Behaviour is entirely client-side — grading still goes through the normal optimistic flow via
 * the `commitGrade` callback.
 */
export function useWriteInReview(opts: { commitGrade: (rating: FsrsRating) => void; reveal: () => void }) {
  const srsStore = useSrsStore();
  const settings = computed(() => srsStore.studySettings.writeInReview);

  const enabledModes = computed<WriteInMode[]>(() => {
    const modes: WriteInMode[] = [];
    if (settings.value.modalitySrs) modes.push('srs');
    if (settings.value.modalityReading) modes.push('reading');
    if (settings.value.modalityMeaning) modes.push('meaning');
    return modes.length ? modes : ['srs'];
  });

  // Any write-in modality on at all? When only standard cards are enabled the feature is fully inert.
  const writeInActive = computed(() => enabledModes.value.some((m) => m !== 'srs'));

  // Equal-split shuffled assignment: draw modes from a bag that refills (reshuffled) when empty,
  // so the rotation is balanced across a batch without being a fixed cycle. Assignments are cached
  // per card so re-shown ("Again") cards keep their style and the UI doesn't flicker between renders.
  const cardModes = new Map<string, WriteInMode>();
  let bag: WriteInMode[] = [];
  function drawMode(): WriteInMode {
    if (!bag.length) bag = shuffleInPlace([...enabledModes.value]);
    return bag.pop()!;
  }
  function cardKey(c: StudyCardDto) {
    return `${c.wordId}-${c.readingIndex}`;
  }
  function modeFor(card: StudyCardDto): WriteInMode {
    const key = cardKey(card);
    const cached = cardModes.get(key);
    if (cached) return cached;
    let mode: WriteInMode;
    if (!writeInActive.value) mode = 'srs';
    else if (card.isNewCard && settings.value.skipNewCards)
      // Not cached: once the card is graded it comes back as a review and must draw a real mode.
      return 'srs';
    else mode = drawMode();
    // Reading mode is pointless for words with no kanji (the shown surface already is the reading).
    // Re-route to meaning if enabled, else fall back to a standard card.
    if (mode === 'reading' && !hasKanji(card.wordTextPlain)) {
      mode = settings.value.modalityMeaning ? 'meaning' : 'srs';
    }
    cardModes.set(key, mode);
    return mode;
  }

  // Re-roll everything if the modality set changes mid-session (e.g. the user edits settings).
  watch(enabledModes, () => {
    cardModes.clear();
    bag = [];
  });

  const currentMode = computed<WriteInMode>(() => (srsStore.currentCard ? modeFor(srsStore.currentCard) : 'srs'));
  const isWriteInCard = computed(() => currentMode.value !== 'srs');
  // Input phase: a write-in card that hasn't been revealed yet.
  const isInputPhase = computed(() => isWriteInCard.value && !srsStore.isFlipped);

  const result = ref<WriteInResult | null>(null);
  const shake = ref(false);
  const gaveUp = ref(false);
  // A transient prompt shown under the field (e.g. when a meaning answer was only filler words).
  const message = ref<string | null>(null);
  let shakeTimer: ReturnType<typeof setTimeout> | null = null;

  // Suggested grade shown on reveal: Good when the answer was correct, Again otherwise.
  const suggestedRating = computed<FsrsRating | null>(() => {
    if (!srsStore.isFlipped || !isWriteInCard.value) return null;
    if (result.value?.ok) return FsrsRating.Good;
    if (result.value || gaveUp.value) return FsrsRating.Again;
    return null;
  });

  // --- Auto-advance -----------------------------------------------------------------------------
  const autoAdvanceRating = ref<FsrsRating | null>(null);
  const autoAdvanceFraction = ref(0);
  let rafId: number | null = null;
  let autoStart = 0;

  function cancelAutoAdvance() {
    if (rafId !== null) {
      cancelAnimationFrame(rafId);
      rafId = null;
    }
    autoAdvanceRating.value = null;
    autoAdvanceFraction.value = 0;
  }

  function armAutoAdvance(rating: FsrsRating) {
    cancelAutoAdvance();
    const seconds = Math.max(0, settings.value.autoAdvanceSeconds);
    autoAdvanceRating.value = rating;
    if (seconds === 0) {
      autoAdvanceFraction.value = 1;
      // Defer one tick so the reveal renders before we advance.
      setTimeout(() => commit(rating), 0);
      return;
    }
    autoStart = performance.now();
    const tick = (t: number) => {
      const elapsed = (t - autoStart) / 1000;
      autoAdvanceFraction.value = Math.min(1, elapsed / seconds);
      if (autoAdvanceFraction.value >= 1) {
        rafId = null;
        commit(rating);
      } else {
        rafId = requestAnimationFrame(tick);
      }
    };
    rafId = requestAnimationFrame(tick);
  }

  function commit(rating: FsrsRating) {
    cancelAutoAdvance();
    opts.commitGrade(rating);
  }

  // --- Actions ----------------------------------------------------------------------------------
  function triggerShake() {
    shake.value = false;
    void nextTick(() => {
      shake.value = true;
      if (shakeTimer) clearTimeout(shakeTimer);
      shakeTimer = setTimeout(() => {
        shake.value = false;
      }, 500);
    });
  }

  function submit(value: string) {
    const card = srsStore.currentCard;
    if (!card || !isInputPhase.value) return;
    const res = currentMode.value === 'meaning' ? checkMeaning(value, card) : checkReading(value, card);

    // Filler-only meaning answer (e.g. "to"): not a real attempt — prompt for a content word and
    // let them retry, without revealing or grading. Same behaviour regardless of the wrong setting.
    if (res.invalid) {
      message.value = 'Type a key word from the meaning — small words like “to” don’t count';
      triggerShake();
      return;
    }

    message.value = null;
    result.value = res;
    if (settings.value.sound) playWriteInChime(res.ok);

    if (res.ok) {
      opts.reveal();
      if (settings.value.autoAdvance) armAutoAdvance(FsrsRating.Good);
      return;
    }

    if (settings.value.wrongBehavior === 'Retry') {
      triggerShake();
      return;
    }
    // Reveal-on-wrong: flip to the answer, suggest Again.
    opts.reveal();
    if (settings.value.autoAdvance && settings.value.autoAdvanceWrong) armAutoAdvance(FsrsRating.Again);
  }

  // The user bailed out of guessing (Reveal button / Esc) — treat it like a wrong answer: reveal,
  // suggest Again, and auto-advance if the user enabled it for wrong answers.
  function giveUp() {
    if (!isInputPhase.value) return;
    gaveUp.value = true;
    if (settings.value.sound) playWriteInChime(false);
    opts.reveal();
    if (settings.value.autoAdvance && settings.value.autoAdvanceWrong) armAutoAdvance(FsrsRating.Again);
  }

  function resetCard() {
    cancelAutoAdvance();
    result.value = null;
    gaveUp.value = false;
    shake.value = false;
    message.value = null;
    if (shakeTimer) {
      clearTimeout(shakeTimer);
      shakeTimer = null;
    }
  }

  // New card → clear transient per-card state.
  watch(
    () => srsStore.currentCard && `${srsStore.currentCard.wordId}-${srsStore.currentCard.readingIndex}`,
    () => {
      resetCard();
    }
  );

  onScopeDispose(() => {
    cancelAutoAdvance();
    if (shakeTimer) clearTimeout(shakeTimer);
  });

  return {
    settings,
    enabledModes,
    writeInActive,
    currentMode,
    isWriteInCard,
    isInputPhase,
    result,
    shake,
    gaveUp,
    message,
    suggestedRating,
    autoAdvanceRating,
    autoAdvanceFraction,
    submit,
    giveUp,
    cancelAutoAdvance,
    resetCard,
  };
}
