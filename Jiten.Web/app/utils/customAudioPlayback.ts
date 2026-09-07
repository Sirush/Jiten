// Plays a user's card audio clip with fallbacks on errors

export type PlaybackFailureReason = 'expired' | 'blocked' | 'failed';
export type PlaybackStart = { ok: true } | { ok: false; reason: PlaybackFailureReason; detail: string };
export type PlaybackFailure = Extract<PlaybackStart, { ok: false }>;

export interface PlaybackHandle {
  /** Resolves once sound is actually playing, or with the reason nothing could play. */
  started: Promise<PlaybackStart>;
  /** Resolves when the clip ended, failed, or stop() was called; never rejects. */
  finished: Promise<void>;
  stop(): void;
}

const URL_STALL_TIMEOUT_MS = 6000;
const BLOB_START_TIMEOUT_MS = 2500;

function describe(err: unknown): string {
  if (err && typeof err === 'object' && 'name' in err) {
    const e = err as { name?: string; message?: string };
    return `${e.name ?? 'Error'}: ${e.message ?? ''}`;
  }
  return String(err);
}

function isAutoplayBlock(err: unknown): boolean {
  return !!err && typeof err === 'object' && (err as { name?: string }).name === 'NotAllowedError';
}

function mediaError(el: HTMLMediaElement): string {
  const e = el.error;
  if (!e) return 'no MediaError';
  const codes: Record<number, string> = { 1: 'ABORTED', 2: 'NETWORK', 3: 'DECODE', 4: 'SRC_NOT_SUPPORTED' };
  return `${codes[e.code] ?? `code ${e.code}`}${e.message ? ` (${e.message})` : ''}`;
}

type RungResult = { started: true } | { started: false; failure: string; blocked: boolean };

export function playCustomAudio(url: string): PlaybackHandle {
  let stopped = false;
  let element: HTMLAudioElement | null = null;
  let source: AudioBufferSourceNode | null = null;
  let context: AudioContext | null = null;
  let blobUrl: string | null = null;

  let resolveFinished!: () => void;
  const finished = new Promise<void>((resolve) => {
    resolveFinished = resolve;
  });

  function cleanup() {
    if (element) {
      element.onended = null;
      element.onerror = null;
      element.onplaying = null;
      element.onprogress = null;
      element.pause();
      element.removeAttribute('src');
      element = null;
    }
    if (source) {
      source.onended = null;
      try {
        source.stop();
      } catch {
        /* already stopped */
      }
      source = null;
    }
    if (context) {
      context.close().catch(() => {});
      context = null;
    }
    if (blobUrl) {
      URL.revokeObjectURL(blobUrl);
      blobUrl = null;
    }
  }

  function stop() {
    if (stopped) return;
    stopped = true;
    cleanup();
    resolveFinished();
  }

  function playElement(src: string, timeoutMs: number): Promise<RungResult> {
    return new Promise((resolve) => {
      let settled = false;
      let timer: ReturnType<typeof setTimeout> | undefined;
      const settle = (result: RungResult) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(result);
      };
      const a = new Audio(src);
      element = a;
      const arm = () => {
        clearTimeout(timer);
        timer = setTimeout(() => {
          if (element === a) cleanup();
          settle({ started: false, failure: `no playback after ${timeoutMs / 1000}s without progress (readyState=${a.readyState}, ${mediaError(a)})`, blocked: false });
        }, timeoutMs);
      };
      arm();
      a.onprogress = () => {
        if (!settled) arm();
      };
      a.onplaying = () => settle({ started: true });
      a.onended = () => {
        settle({ started: true });
        if (element === a) stop();
      };
      // Before start the error fails this rung; after start it ends the clip, or finished() never settles.
      a.onerror = () => {
        const text = mediaError(a);
        if (settled) {
          if (element === a) stop();
          return;
        }
        if (element === a) cleanup();
        settle({ started: false, failure: text, blocked: false });
      };
      a.play()
        .then(() => settle({ started: true }))
        .catch((err) => {
          if (element === a) cleanup();
          settle({ started: false, failure: `play() rejected: ${describe(err)}`, blocked: isAutoplayBlock(err) });
        });
    });
  }

  async function fetchBytes(): Promise<{ bytes: ArrayBuffer; type: string } | { failure: string; expired: boolean }> {
    try {
      const res = await fetch(url, { cache: 'no-store' });
      if (!res.ok) return { failure: `fetch status ${res.status}`, expired: res.status === 401 || res.status === 403 };
      return { bytes: await res.arrayBuffer(), type: res.headers.get('content-type') ?? '' };
    } catch (err) {
      // A TypeError with no status is what a missing Access-Control-Allow-Origin looks like from JS.
      const hint = err instanceof TypeError ? ' (network error or CDN sent no CORS headers)' : '';
      return { failure: `fetch failed: ${describe(err)}${hint}`, expired: false };
    }
  }

  async function playDecoded(bytes: ArrayBuffer): Promise<RungResult> {
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return { started: false, failure: 'no AudioContext', blocked: false };
    const ctx = new Ctx();
    context = ctx;
    try {
      const buffer = await new Promise<AudioBuffer>((resolve, reject) => {
        const maybe = ctx.decodeAudioData(bytes.slice(0), resolve, reject);
        if (maybe && typeof (maybe as Promise<AudioBuffer>).then === 'function') (maybe as Promise<AudioBuffer>).then(resolve, reject);
      });
      if (stopped) return { started: false, failure: 'stopped', blocked: false };
      if (ctx.state === 'suspended') {
        // resume() stays pending until a user gesture when autoplay is blocked; stop() must still win.
        const resumed = await Promise.race([ctx.resume().then(() => true), finished.then(() => false)]);
        if (!resumed || ctx.state !== 'running') {
          if (context === ctx) cleanup();
          return { started: false, failure: `AudioContext ${ctx.state}`, blocked: !stopped };
        }
      }
      const node = ctx.createBufferSource();
      node.buffer = buffer;
      node.connect(ctx.destination);
      node.onended = () => {
        if (source === node) stop();
      };
      source = node;
      node.start();
      return { started: true };
    } catch (err) {
      if (context === ctx) cleanup();
      return { started: false, failure: `decodeAudioData: ${describe(err)}`, blocked: false };
    }
  }

  const started = (async (): Promise<PlaybackStart> => {
    const failures: string[] = [];
    const fail = (reason: PlaybackFailureReason): PlaybackStart => {
      stop();
      return { ok: false, reason, detail: failures.join('; ') };
    };

    const direct = await playElement(url, URL_STALL_TIMEOUT_MS);
    if (direct.started) return { ok: true };
    failures.push(`url: ${direct.failure}`);
    // Autoplay policy blocks every rung alike; climbing further only downloads the clip for nothing.
    if (direct.blocked) return fail('blocked');
    if (stopped) return fail('failed');

    const fetched = await fetchBytes();
    if ('failure' in fetched) {
      failures.push(fetched.failure);
      return fail(fetched.expired ? 'expired' : 'failed');
    }
    if (stopped) return fail('failed');

    blobUrl = URL.createObjectURL(new Blob([fetched.bytes], fetched.type ? { type: fetched.type } : undefined));
    const viaBlob = await playElement(blobUrl, BLOB_START_TIMEOUT_MS);
    if (viaBlob.started) return { ok: true };
    failures.push(`blob: ${viaBlob.failure}`);
    if (viaBlob.blocked) return fail('blocked');
    if (stopped) return fail('failed');

    const decoded = await playDecoded(fetched.bytes);
    if (decoded.started) return { ok: true };
    failures.push(`webaudio: ${decoded.failure}`);
    return fail(decoded.blocked ? 'blocked' : 'failed');
  })();

  return { started, finished, stop };
}

const FAILURE_TOASTS = {
  blocked: {
    severity: 'warn' as const,
    summary: 'Audio autoplay is blocked',
    detail: 'Your browser will not play sound until you tap or click the page. Press play to hear the clip.',
    life: 6000,
  },
  expired: {
    severity: 'error' as const,
    summary: "Couldn't play the audio clip",
    detail: 'The clip link has expired and could not be refreshed. Please reload the page and try again.',
    life: 6000,
  },
  failed: {
    severity: 'error' as const,
    summary: "Couldn't play the audio clip",
    detail: 'Your browser could not play this clip. The details are available in your browser console.',
    life: 6000,
  },
};

export function clipFailureToast(result: PlaybackFailure) {
  return FAILURE_TOASTS[result.reason];
}

export function logClipFailure(url: string, result: PlaybackFailure) {
  const log = result.reason === 'blocked' ? console.warn : console.error;
  log(`Card audio clip failed (${result.reason}): ${result.detail}\n${url}`);
}
