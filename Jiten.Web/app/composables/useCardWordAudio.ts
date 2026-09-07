import { useToast } from 'primevue/usetoast';
import type { CardMediaDto } from '~/types';
import { clipFailureToast, logClipFailure, playCustomAudio, type PlaybackFailure, type PlaybackHandle } from '~/utils/customAudioPlayback';

interface PlayWordOptions {
  wordId: number;
  readingIndex: number;
  fallbackText?: string;
  media: CardMediaDto | null | undefined;
  // Called once when the custom audio fails to load (typically an expired signed URL) to obtain a fresh CardMediaDto.
  onExpired?: () => Promise<CardMediaDto | null>;
}

interface PlayClipOptions {
  media: CardMediaDto | null | undefined;
  onExpired?: () => Promise<CardMediaDto | null>;
}

export function useCardWordAudio() {
  const tts = useTts();
  const toast = useToast();
  let active: PlaybackHandle | null = null;
  const customPlaying = ref(false);

  function stopCustom() {
    active?.stop();
    active = null;
    customPlaying.value = false;
  }

  function stop() {
    stopCustom();
    tts.stop();
  }

  function reportFailure(url: string, result: PlaybackFailure) {
    logClipFailure(url, result);
    toast.add(clipFailureToast(result));
  }

  async function startClip(opts: PlayClipOptions): Promise<PlaybackHandle | null> {
    const media = opts.media;
    if (!media?.url) return null;

    let url = media.url;
    let handle = playCustomAudio(url);
    active = handle;
    customPlaying.value = true;
    let result = await handle.started;

    if (!result.ok && result.reason !== 'blocked' && active === handle) {
      const fresh = await opts.onExpired?.();
      if (active === handle && fresh?.url && fresh.url !== url) {
        url = fresh.url;
        handle = playCustomAudio(url);
        active = handle;
        result = await handle.started;
      }
    }

    if (active !== handle) return null;
    if (!result.ok) {
      reportFailure(url, result);
      stopCustom();
      return null;
    }

    handle.finished.then(() => {
      if (active === handle) {
        active = null;
        customPlaying.value = false;
      }
    });
    return handle;
  }

  async function playWord(opts: PlayWordOptions) {
    stop();
    if (opts.media?.url) {
      await startClip(opts);
      return;
    }
    tts.speakWord(opts.wordId, opts.readingIndex, opts.fallbackText);
  }

  async function playCustomToEnd(opts: PlayClipOptions): Promise<boolean> {
    stop();
    const handle = await startClip(opts);
    if (!handle) return false;
    await handle.finished;
    return true;
  }

  // True while either the custom clip or the TTS word audio is sounding. Drives the play button's
  // active state and the autoplay-then-sentence chaining.
  const isWordPlaying = computed(() => customPlaying.value || tts.isAnyPlaying.value);

  return { playWord, playCustomToEnd, stop, isWordPlaying, customPlaying };
}
