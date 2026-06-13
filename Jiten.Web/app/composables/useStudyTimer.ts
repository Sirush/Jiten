import type { Ref } from 'vue';
import { FsrsRating } from '~/types';
import { useSrsStore } from '~/stores/srsStore';

export type TimerPhase = 'idle' | 'reveal' | 'answer' | 'armed';

export interface StudyTimerCallbacks {
  // Whether timed mode is on for this session (the stopwatch toggle).
  active: Ref<boolean>;
  // When true, the countdown freezes (e.g. while the in-study settings dialog is open).
  suspended?: Ref<boolean>;
  // Reveal the current card (flip to the answer).
  onReveal: () => void;
  // Grade the current card.
  onGrade: (rating: FsrsRating) => void;
}

// How long the soft-fail "Again in N" grace lasts before it auto-grades.
const GRACE_MS = 3000;
// Extra time granted to the very first card's question timer to absorb page-load/render jank.
const FIRST_CARD_GRACE_MS = 1000;
// Tick resolution for the countdown ring.
const TICK_MS = 100;

/**
 * "Speed Focus" timed review. Mirrors useStudyKeyboard's shape: reads session state from the store,
 * drives a per-card countdown, and invokes reveal/grade via callbacks. Pure focus aid — it never
 * touches scheduling. See PLAN i-want-to-add-twinkling-clock for the behaviour model.
 */
export function useStudyTimer(cb: StudyTimerCallbacks) {
  const store = useSrsStore();

  const phase = ref<TimerPhase>('idle');
  const remainingMs = ref(0);
  const durationMs = ref(0);
  // True while the soft-fail / fail-learn "Again" countdown is running.
  const armedLocked = ref(false);

  let intervalId: ReturnType<typeof setInterval> | null = null;
  let deadline = 0;
  // The clock freezes when the tab is hidden OR the user manually paused (P). Both are tracked
  // independently so toggling one doesn't clobber the other; `frozen` is whether the clock is stopped now.
  let hiddenPause = false;
  const manualPaused = ref(false);
  let frozen = false;
  let pausedRemaining = 0;
  // cardShownAt of the first card this page showed — its question timer gets FIRST_CARD_GRACE_MS extra.
  let firstCardStamp: number | null = null;

  const cfg = () => store.studySettings.timedReview;

  const fraction = computed(() => (durationMs.value > 0 ? Math.max(0, remainingMs.value / durationMs.value) : 0));
  const secondsLeft = computed(() => Math.ceil(remainingMs.value / 1000));
  const armed = computed(() => phase.value === 'armed');

  // --- Alert sound (WebAudio beep, no asset) ------------------------------------------------------
  // iOS/Safari (and Chrome's autoplay policy) start an AudioContext suspended and only let it produce
  // sound after a user gesture. Since our beep fires from a timer (not a gesture), we create + unlock
  // the context on the session's first tap/key/click, then later beeps are audible everywhere.
  let audioCtx: AudioContext | null = null;
  function getCtx(): AudioContext | null {
    if (audioCtx) return audioCtx;
    const Ctor = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctor) return null;
    try {
      audioCtx = new Ctor();
    } catch {
      return null;
    }
    return audioCtx;
  }
  function unlockAudio() {
    const ctx = getCtx();
    if (!ctx) return;
    if (ctx.state === 'suspended') void ctx.resume();
    // Play a 1-sample silent buffer — the canonical trick that unlocks audio on older iOS.
    try {
      const src = ctx.createBufferSource();
      src.buffer = ctx.createBuffer(1, 1, 22050);
      src.connect(ctx.destination);
      src.start(0);
    } catch {
      /* best-effort */
    }
  }
  function playBeep() {
    if (!cfg().alertSound) return;
    const ctx = getCtx();
    if (!ctx) return;
    try {
      if (ctx.state === 'suspended') void ctx.resume();
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'sine';
      osc.frequency.value = 880;
      const now = ctx.currentTime;
      gain.gain.setValueAtTime(0.0001, now);
      gain.gain.exponentialRampToValueAtTime(0.18, now + 0.01);
      gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.18);
      osc.connect(gain).connect(ctx.destination);
      osc.start(now);
      osc.stop(now + 0.2);
    } catch {
      /* audio is best-effort */
    }
  }

  // --- Tick loop ----------------------------------------------------------------------------------
  function stopTick() {
    if (intervalId) {
      clearInterval(intervalId);
      intervalId = null;
    }
  }

  function startTick() {
    stopTick();
    intervalId = setInterval(tick, TICK_MS);
  }

  function tick() {
    if (frozen) return;
    const remaining = deadline - Date.now();
    remainingMs.value = Math.max(0, remaining);
    if (remaining <= 0) {
      stopTick();
      // Run the expiry action OUTSIDE this setInterval callback. handleExpire auto-grades, which
      // mutates store state the restart watcher responds to; doing that re-entrantly from inside the
      // interval tick left the next card without a timer. A microtask hop makes it a clean grade.
      const expiringPhase = phase.value;
      queueMicrotask(() => {
        if (!frozen && phase.value === expiringPhase) handleExpire();
      });
    }
  }

  function beginCountdown(p: 'reveal' | 'answer', ms: number) {
    phase.value = p;
    durationMs.value = ms;
    remainingMs.value = ms;
    deadline = Date.now() + ms;
    armedLocked.value = false;
    setGradeLock(false);
    if (shouldFreeze()) {
      frozen = true;
      pausedRemaining = ms;
      return;
    }
    frozen = false;
    if (ms <= 0) {
      // 0s answer timer = skip the back: fire before the next paint instead of flashing the answer.
      queueMicrotask(() => {
        if (!frozen && phase.value === p) handleExpire();
      });
    } else {
      startTick();
    }
  }

  function setGradeLock(v: boolean) {
    store.gradeLock = v;
  }

  // Enter the "Again in N" countdown. locked = the fail-and-learn absorption window (no override).
  function startArm(locked: boolean) {
    phase.value = 'armed';
    durationMs.value = GRACE_MS;
    remainingMs.value = GRACE_MS;
    deadline = Date.now() + GRACE_MS;
    armedLocked.value = locked;
    setGradeLock(locked);
    playBeep(); // "time's up" — the answer/question timer just elapsed and Again is now arming
    if (shouldFreeze()) {
      frozen = true;
      pausedRemaining = GRACE_MS;
    } else {
      frozen = false;
      startTick();
    }
  }

  function handleExpire() {
    const c = cfg();
    if (phase.value === 'reveal') {
      if (c.revealAction === 'Reveal') {
        playBeep(); // time's up
        cb.onReveal(); // flips → isFlipped watcher starts the answer phase
      } else if (c.revealAction === 'FailLearn') {
        cb.onReveal();
        // The auto-fail must match the answer action: hard = immediate (no soft timer), soft = locked "auto in N".
        if (c.answerAction === 'HardFail') {
          playBeep(); // time's up
          phase.value = 'idle';
          cb.onGrade(FsrsRating.Again);
          scheduleAutoRestart();
        } else {
          startArm(true); // beeps
        }
      } else {
        playBeep(); // Nudge / alert-only: the beep is the whole point, fired when time runs out
        phase.value = 'idle';
      }
    } else if (phase.value === 'answer') {
      if (c.answerAction === 'HardFail') {
        playBeep(); // time's up
        phase.value = 'idle';
        cb.onGrade(FsrsRating.Again); // advances → watcher restarts the next card's front phase
        scheduleAutoRestart();
      } else {
        startArm(false); // beeps
      }
    } else if (phase.value === 'armed') {
      setGradeLock(false);
      phase.value = 'idle';
      cb.onGrade(FsrsRating.Again);
      scheduleAutoRestart();
    }
  }

  // Belt-and-suspenders: after an auto-grade made from inside the timer's own flow, make sure the
  // next card's timer starts even if the reactive restart watcher doesn't fire. Runs after the
  // current flush; the `phase === 'idle'` guard means it's a no-op when the watcher already restarted.
  function scheduleAutoRestart() {
    nextTick(() => {
      if (phase.value === 'idle' && cb.active.value && store.currentCard && !store.isSessionComplete && !store.isFlipped) {
        startPhase();
      }
    });
  }

  // --- Phase selection from store state -----------------------------------------------------------
  function cancel() {
    stopTick();
    setGradeLock(false);
    armedLocked.value = false;
    manualPaused.value = false;
    frozen = false;
    phase.value = 'idle';
    remainingMs.value = 0;
    durationMs.value = 0;
  }

  function startPhase() {
    stopTick();
    armedLocked.value = false;
    setGradeLock(false);
    const c = cfg();
    if (!cb.active.value || !store.currentCard || store.isSessionComplete) {
      phase.value = 'idle';
      return;
    }
    // Don't rush brand-new cards (neither front nor back) when the user opts out.
    if (c.skipNewCards && store.currentCard.isNewCard) {
      phase.value = 'idle';
      remainingMs.value = 0;
      return;
    }
    if (!store.isFlipped) {
      if (!c.revealEnabled) {
        phase.value = 'idle';
        remainingMs.value = 0;
        return;
      }
      // Remember the first card so its question timer (and only its) gets the load-jank grace.
      if (firstCardStamp === null) firstCardStamp = store.cardShownAt;
      const graceMs = store.cardShownAt === firstCardStamp ? FIRST_CARD_GRACE_MS : 0;
      beginCountdown('reveal', Math.max(1, c.revealSeconds) * 1000 + graceMs);
    } else {
      if (!c.answerEnabled) {
        phase.value = 'idle';
        remainingMs.value = 0;
        return;
      }
      beginCountdown('answer', Math.max(0, c.answerSeconds) * 1000);
    }
  }

  // --- Freeze: clock stops while the tab is hidden, the settings dialog is open, or manually paused -
  function shouldFreeze() {
    return hiddenPause || manualPaused.value || (cb.suspended?.value ?? false);
  }
  // Reconcile the running clock with the current freeze sources, preserving the remaining time.
  function applyFreeze() {
    const want = shouldFreeze();
    if (want && !frozen) {
      frozen = true;
      if (phase.value === 'idle') return;
      pausedRemaining = Math.max(0, deadline - Date.now());
      stopTick();
    } else if (!want && frozen) {
      frozen = false;
      if (phase.value === 'idle') return;
      deadline = Date.now() + pausedRemaining;
      startTick();
    }
  }
  function onVisibility() {
    hiddenPause = document.hidden;
    applyFreeze();
  }
  // Manual pause toggle (the P keybind / UI). Returns the new paused state.
  function togglePause() {
    manualPaused.value = !manualPaused.value;
    applyFreeze();
    return manualPaused.value;
  }

  // --- Watchers -----------------------------------------------------------------------------------
  // One watcher drives every phase transition: it fires whenever the shown card (cardShownAt, bumped
  // on each fresh card and after every grade) OR its face (isFlipped) changes. A single array watcher
  // is used deliberately — two separate watchers miss the auto-hard-fail case, where the same tick
  // flips isFlipped true→false (net-unchanged, so an isFlipped watcher never fires) while cardShownAt
  // advances. The only transition we must NOT restart on is a fail-learn forced reveal, which flips
  // the card without advancing it and must keep its "Again" arm.
  watch([() => store.cardShownAt, () => store.isFlipped], ([shownAt], [prevShownAt]) => {
    if (phase.value === 'armed' && shownAt === prevShownAt) return; // fail-learn forced reveal → keep arm
    startPhase();
  });

  watch(cb.active, (on) => {
    if (!on) manualPaused.value = false; // a fresh activation shouldn't inherit a stale pause
    startPhase();
  });
  // Freeze/thaw when an external blocker (the settings dialog) opens or closes.
  if (cb.suspended) watch(cb.suspended, () => applyFreeze());
  // Settings edits mid-session (durations, actions, toggles) re-arm the current phase.
  watch(
    () => store.studySettings.timedReview,
    () => startPhase(),
    { deep: true }
  );
  watch(
    () => store.isSessionComplete,
    (done) => {
      if (done) cancel();
    }
  );

  const onFirstGesture = () => unlockAudio();
  onMounted(() => {
    document.addEventListener('visibilitychange', onVisibility);
    // Unlock audio on the first real interaction so timer-fired beeps work on iOS/Safari.
    window.addEventListener('pointerdown', onFirstGesture, { once: true, passive: true });
    window.addEventListener('keydown', onFirstGesture, { once: true });
    window.addEventListener('touchstart', onFirstGesture, { once: true, passive: true });
    startPhase();
  });
  onUnmounted(() => {
    document.removeEventListener('visibilitychange', onVisibility);
    window.removeEventListener('pointerdown', onFirstGesture);
    window.removeEventListener('keydown', onFirstGesture);
    window.removeEventListener('touchstart', onFirstGesture);
    cancel();
  });

  return {
    phase: readonly(phase),
    remainingMs: readonly(remainingMs),
    fraction,
    secondsLeft,
    armed,
    armedLocked: readonly(armedLocked),
    paused: readonly(manualPaused),
    togglePause,
  };
}
