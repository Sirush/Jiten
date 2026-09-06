import { onBeforeUnmount, ref, type Ref } from 'vue';

interface YTPlayer {
  playVideo(): void;
  pauseVideo(): void;
  seekTo(seconds: number, allowSeekAhead: boolean): void;
  getCurrentTime(): number;
  getPlayerState(): number;
  setPlaybackRate(rate: number): void;
  destroy(): void;
}

interface YTNamespace {
  Player: new (element: HTMLElement, options: Record<string, unknown>) => YTPlayer;
  PlayerState: { PLAYING: number; PAUSED: number; ENDED: number };
}

declare global {
  interface Window {
    YT?: YTNamespace;
    onYouTubeIframeAPIReady?: () => void;
  }
}

let apiPromise: Promise<YTNamespace> | null = null;

function loadApi(): Promise<YTNamespace> {
  if (window.YT?.Player) return Promise.resolve(window.YT);
  if (apiPromise) return apiPromise;
  apiPromise = new Promise((resolve) => {
    const previous = window.onYouTubeIframeAPIReady;
    window.onYouTubeIframeAPIReady = () => {
      previous?.();
      resolve(window.YT!);
    };
    const script = document.createElement('script');
    script.src = 'https://www.youtube.com/iframe_api';
    script.async = true;
    document.head.appendChild(script);
  });
  return apiPromise;
}

const POLL_MS = 200;
const SEEK_SETTLE_SECONDS = 1.5;
const SEEK_SETTLE_TIMEOUT_MS = 3000;

/**
 * Embeds one video through the official player and keeps `currentTime` fresh while it plays. The player is
 * mounted into `container` on demand so the page can render before the API script arrives.
 */
export function useYouTubePlayer(container: Ref<HTMLElement | null>) {
  const ready = ref(false);
  const playing = ref(false);
  const ended = ref(false);
  // Error 101/150 = embedding disabled by the uploader, 100 = gone; either way the embed is dead
  const embedBlocked = ref(false);
  const currentTime = ref(0);
  const playbackRate = ref(1);

  let player: YTPlayer | null = null;
  let timer: ReturnType<typeof setInterval> | null = null;
  // The iframe keeps reporting the pre-seek position until it has buffered the target, so those reads are dropped.
  let pendingSeek: { seconds: number; until: number } | null = null;

  const stopPolling = () => {
    if (timer) clearInterval(timer);
    timer = null;
  };

  const poll = () => {
    if (!player) return;
    try {
      const t = player.getCurrentTime();
      if (pendingSeek) {
        if (Math.abs(t - pendingSeek.seconds) > SEEK_SETTLE_SECONDS && performance.now() < pendingSeek.until) return;
        pendingSeek = null;
      }
      currentTime.value = t;
    } catch {
      // The iframe can be gone mid-navigation
    }
  };

  const startPolling = () => {
    stopPolling();
    timer = setInterval(poll, POLL_MS);
  };

  const mount = async (videoId: string) => {
    if (!container.value || player) return;
    const yt = await loadApi();
    if (!container.value) return;
    player = new yt.Player(container.value, {
      videoId,
      host: 'https://www.youtube-nocookie.com',
      playerVars: { rel: 0, playsinline: 1, modestbranding: 1 },
      events: {
        onReady: () => {
          ready.value = true;
        },
        onError: (event: { data: number }) => {
          if ([100, 101, 150].includes(event.data)) {
            embedBlocked.value = true;
            stopPolling();
          }
        },
        onStateChange: (event: { data: number }) => {
          playing.value = event.data === yt.PlayerState.PLAYING;
          ended.value = event.data === yt.PlayerState.ENDED;
          if (playing.value) startPolling();
          else {
            poll();
            stopPolling();
          }
        },
      },
    });
  };

  const play = () => player?.playVideo();
  const pause = () => player?.pauseVideo();
  const seek = (seconds: number, andPlay = true) => {
    if (!player) return;
    const target = Math.max(0, seconds);
    pendingSeek = { seconds: target, until: performance.now() + SEEK_SETTLE_TIMEOUT_MS };
    player.seekTo(target, true);
    currentTime.value = target;
    if (andPlay) player.playVideo();
  };
  const setRate = (rate: number) => {
    playbackRate.value = rate;
    player?.setPlaybackRate(rate);
  };

  const destroy = () => {
    stopPolling();
    try {
      player?.destroy();
    } catch {
      // Already torn down with the page
    }
    player = null;
  };

  onBeforeUnmount(destroy);

  return { ready, playing, ended, embedBlocked, currentTime, playbackRate, mount, destroy, play, pause, seek, setRate };
}
