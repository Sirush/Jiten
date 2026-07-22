import type { CardMediaDto } from '~/types';

interface PlayWordOptions {
  wordId: number;
  readingIndex: number;
  fallbackText?: string;
  media: CardMediaDto | null | undefined;
  // Called once when the custom audio fails to load (typically an expired signed URL) to obtain a
  // fresh CardMediaDto. Returning null gives up on custom audio and falls back to TTS.
  onExpired?: () => Promise<CardMediaDto | null>;
}

export function useCardWordAudio() {
  const tts = useTts();
  let audio: HTMLAudioElement | null = null;
  const customPlaying = ref(false);

  function stopCustom() {
    if (audio) {
      audio.onended = null;
      audio.onerror = null;
      audio.pause();
      audio = null;
    }
    customPlaying.value = false;
  }

  function stop() {
    stopCustom();
    tts.stop();
  }

  // Resolves true once playback starts, false if it could not start (bad/expired URL).
  function playUrl(url: string): Promise<boolean> {
    return new Promise((resolve) => {
      let settled = false;
      const settle = (ok: boolean) => {
        if (!settled) {
          settled = true;
          resolve(ok);
        }
      };
      const a = new Audio(url);
      audio = a;
      customPlaying.value = true;
      a.onended = () => {
        if (audio === a) {
          customPlaying.value = false;
          audio = null;
        }
      };
      a.onerror = () => {
        if (audio === a) {
          customPlaying.value = false;
          audio = null;
        }
        settle(false);
      };
      a.play()
        .then(() => settle(true))
        .catch(() => {
          if (audio === a) {
            customPlaying.value = false;
            audio = null;
          }
          settle(false);
        });
    });
  }

  async function playWord(opts: PlayWordOptions) {
    stop();
    const media = opts.media;
    if (media?.url) {
      const ok = await playUrl(media.url);
      if (ok) return;
      // One retry with a fresh signed URL, then give up on custom audio for this play.
      const fresh = await opts.onExpired?.();
      if (fresh?.url && fresh.url !== media.url) {
        const retryOk = await playUrl(fresh.url);
        if (retryOk) return;
      }
      tts.speakWord(opts.wordId, opts.readingIndex, opts.fallbackText);
      return;
    }
    tts.speakWord(opts.wordId, opts.readingIndex, opts.fallbackText);
  }

  // Resolves true once the clip has played to its end, false if it never started.
  function playUrlToEnd(url: string): Promise<boolean> {
    return new Promise((resolve) => {
      let settled = false;
      const settle = (ok: boolean) => {
        if (!settled) {
          settled = true;
          resolve(ok);
        }
      };
      const a = new Audio(url);
      audio = a;
      customPlaying.value = true;
      a.onended = () => {
        if (audio === a) {
          customPlaying.value = false;
          audio = null;
        }
        settle(true);
      };
      a.onerror = () => {
        if (audio === a) {
          customPlaying.value = false;
          audio = null;
        }
        settle(false);
      };
      a.play().catch(() => {
        if (audio === a) {
          customPlaying.value = false;
          audio = null;
        }
        settle(false);
      });
    });
  }

  // Plays the custom clip and resolves true once it finishes, false if it could not play (even after
  // one signed-URL refresh). Unlike playWord it never falls back to TTS — the caller decides.
  async function playCustomToEnd(opts: { media: CardMediaDto | null | undefined; onExpired?: () => Promise<CardMediaDto | null> }): Promise<boolean> {
    stop();
    const media = opts.media;
    if (!media?.url) return false;
    if (await playUrlToEnd(media.url)) return true;
    const fresh = await opts.onExpired?.();
    if (fresh?.url && fresh.url !== media.url) {
      return await playUrlToEnd(fresh.url);
    }
    return false;
  }

  // True while either the custom clip or the TTS word audio is sounding. Drives the play button's
  // active state and the autoplay-then-sentence chaining.
  const isWordPlaying = computed(() => customPlaying.value || tts.isAnyPlaying.value);

  return { playWord, playCustomToEnd, stop, isWordPlaying, customPlaying };
}
