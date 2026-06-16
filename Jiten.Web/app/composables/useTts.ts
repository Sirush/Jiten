const browserSupported = ref(false);
let japaneseVoice: SpeechSynthesisVoice | null = null;

let activeAudio: HTMLAudioElement | null = null;
let activeAbort: AbortController | null = null;
let loadingTimer: ReturnType<typeof setTimeout> | null = null;
const activeText = ref<string | null>(null);
const activeState = ref<'loading' | 'playing' | null>(null);

function scoreVoice(v: SpeechSynthesisVoice): number {
  const name = v.name.toLowerCase();
  if (name.includes('neural') || name.includes('online')) return 3;
  if (name.includes('natural')) return 2;
  if (v.localService === false) return 1;
  return 0;
}

function findJapaneseVoice() {
  const voices = speechSynthesis.getVoices();
  const jaVoices = voices.filter(v => v.lang.startsWith('ja'));
  if (jaVoices.length === 0) return;
  jaVoices.sort((a, b) => scoreVoice(b) - scoreVoice(a));
  japaneseVoice = jaVoices[0];
}

if (typeof window !== 'undefined' && 'speechSynthesis' in window) {
  browserSupported.value = true;
  findJapaneseVoice();
  speechSynthesis.addEventListener('voiceschanged', findJapaneseVoice);
}

function reset() {
  if (loadingTimer) { clearTimeout(loadingTimer); loadingTimer = null; }
  if (activeAbort) { activeAbort.abort(); activeAbort = null; }
  if (activeAudio) { activeAudio.onended = null; activeAudio.onerror = null; activeAudio.pause(); activeAudio = null; }
  if (browserSupported.value) speechSynthesis.cancel();
  activeText.value = null;
  activeState.value = null;
}

export function stopTts() { reset(); }

export type TtsType = 'word' | 'sentence';

export function useTts(text?: Ref<string> | string, type: TtsType = 'word') {
  const store = useJitenStore();
  const authStore = useAuthStore();
  const config = useRuntimeConfig();

  const resolvedText = computed(() => typeof text === 'string' ? text : text?.value ?? '');

  const isServerMode = computed(() => store.ttsVoice !== 'system');
  const isSupported = computed(() => isServerMode.value || browserSupported.value);
  const isActive = computed(() => resolvedText.value !== '' && activeText.value === resolvedText.value);
  const isSpeaking = computed(() => isActive.value && activeState.value === 'playing');
  const isLoading = computed(() => isActive.value && activeState.value === 'loading');
  const isAnyPlaying = computed(() => activeState.value === 'playing' || activeState.value === 'loading');

  function speakWord(wordId: number, readingIndex: number, fallbackText?: string) {
    if (isServerMode.value) {
      const url = `${config.public.baseURL}tts/word/${wordId}/${readingIndex}?voice=${store.ttsVoice}`;
      playServer(fallbackText ?? `${wordId}`, url);
    } else {
      speakBrowser(fallbackText ?? '');
    }
  }

  function speakSentence(sentenceId: number, fallbackText?: string) {
    if (isServerMode.value) {
      const url = `${config.public.baseURL}tts/sentence/${sentenceId}?voice=${store.ttsVoice}`;
      playServer(fallbackText ?? `s${sentenceId}`, url);
    } else {
      speakBrowser(fallbackText ?? '');
    }
  }

  function speakCustomSentence(userExampleSentenceId: number, fallbackText?: string) {
    if (isServerMode.value) {
      const url = `${config.public.baseURL}tts/custom-sentence/${userExampleSentenceId}?voice=${store.ttsVoice}`;
      playServer(fallbackText ?? `c${userExampleSentenceId}`, url, true);
    } else {
      speakBrowser(fallbackText ?? '');
    }
  }

  function speak(inputText?: string) {
    const t = inputText ?? resolvedText.value;
    if (!t) return;
    speakBrowser(t);
  }

  function speakBrowser(t: string) {
    if (!browserSupported.value || !t) return;
    reset();
    activeText.value = t;
    activeState.value = 'playing';
    const utterance = new SpeechSynthesisUtterance(t);
    utterance.lang = 'ja-JP';
    if (japaneseVoice) utterance.voice = japaneseVoice;
    utterance.onend = () => reset();
    utterance.onerror = () => reset();
    speechSynthesis.speak(utterance);
  }

  async function playServer(textKey: string, url: string, withAuth = false) {
    reset();
    const abort = new AbortController();
    activeAbort = abort;
    activeText.value = textKey;
    loadingTimer = setTimeout(() => { activeState.value = 'loading'; }, 200);

    try {
      const headers: Record<string, string> = {};
      if (withAuth && authStore.accessToken) headers.Authorization = `Bearer ${authStore.accessToken}`;
      const response = await fetch(url, { signal: abort.signal, headers });
      if (!response.ok) throw new Error(`TTS failed: ${response.status}`);
      const blob = await response.blob();
      if (abort.signal.aborted) return;
      const blobUrl = URL.createObjectURL(blob);
      const audio = new Audio(blobUrl);

      if (loadingTimer) { clearTimeout(loadingTimer); loadingTimer = null; }
      activeAudio = audio;
      activeState.value = 'playing';
      audio.onended = () => { reset(); URL.revokeObjectURL(blobUrl); };
      audio.onerror = () => { reset(); URL.revokeObjectURL(blobUrl); };
      await audio.play();
    } catch (e: any) {
      if (e?.name === 'AbortError') return;
      reset();
    }
  }

  return { speak, speakWord, speakSentence, speakCustomSentence, stop: reset, isSpeaking, isAnyPlaying, isSupported, isLoading };
}
