import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { clipFailureToast, playCustomAudio } from '~/utils/customAudioPlayback';

type Behaviour = 'plays' | 'errors' | 'blocked' | 'stalls' | 'errorsMidway';

class FakeAudio {
  static instances: FakeAudio[] = [];
  static behaviourFor: (src: string) => Behaviour = () => 'plays';
  src: string;
  readyState = 0;
  error: { code: number; message: string } | null = null;
  paused = true;
  onended: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onplaying: (() => void) | null = null;
  onprogress: (() => void) | null = null;

  constructor(src: string) {
    this.src = src;
    FakeAudio.instances.push(this);
  }

  pause() {
    this.paused = true;
  }

  removeAttribute() {}

  play(): Promise<void> {
    const behaviour = FakeAudio.behaviourFor(this.src);
    if (behaviour === 'blocked') {
      const err = new Error('play() failed because the user did not interact with the document first');
      err.name = 'NotAllowedError';
      return Promise.reject(err);
    }
    if (behaviour === 'errors') {
      this.error = { code: 4, message: 'MEDIA_ELEMENT_ERROR: Format error' };
      queueMicrotask(() => this.onerror?.());
      return Promise.reject(Object.assign(new Error('no supported source'), { name: 'NotSupportedError' }));
    }
    if (behaviour === 'stalls') return new Promise(() => {});
    this.paused = false;
    queueMicrotask(() => this.onplaying?.());
    return Promise.resolve();
  }

  end() {
    this.onended?.();
  }

  failMidway() {
    this.error = { code: 2, message: 'network' };
    this.onerror?.();
  }
}

class FakeBufferSource {
  buffer: unknown = null;
  onended: (() => void) | null = null;
  connect() {}
  start() {}
  stop() {}
}

class FakeAudioContext {
  static created: FakeAudioContext[] = [];
  static initialState: 'running' | 'suspended' = 'running';
  state: 'running' | 'suspended' | 'closed' = FakeAudioContext.initialState;
  destination = {};
  lastSource: FakeBufferSource | null = null;

  constructor() {
    FakeAudioContext.created.push(this);
  }

  decodeAudioData(_bytes: ArrayBuffer, ok: (b: unknown) => void) {
    ok({});
    return undefined;
  }

  createBufferSource() {
    this.lastSource = new FakeBufferSource();
    return this.lastSource;
  }

  resume(): Promise<void> {
    return new Promise(() => {});
  }

  close() {
    this.state = 'closed';
    return Promise.resolve();
  }
}

const fetchMock = vi.fn();

async function flush() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

beforeEach(() => {
  vi.useFakeTimers();
  FakeAudio.instances = [];
  FakeAudio.behaviourFor = () => 'plays';
  FakeAudioContext.created = [];
  FakeAudioContext.initialState = 'running';
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({ ok: true, status: 200, headers: { get: () => 'audio/mpeg' }, arrayBuffer: async () => new ArrayBuffer(8) });
  vi.stubGlobal('Audio', FakeAudio);
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('window', { AudioContext: FakeAudioContext });
  vi.stubGlobal('Blob', class {
    constructor(public parts: unknown[], public opts?: { type?: string }) {}
  });
  vi.stubGlobal('URL', { createObjectURL: () => 'blob:clip', revokeObjectURL: () => {} });
  vi.spyOn(console, 'error').mockImplementation(() => {});
  vi.spyOn(console, 'warn').mockImplementation(() => {});
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('playCustomAudio', () => {
  it('plays straight from the URL and finishes when the element ends', async () => {
    const handle = playCustomAudio('https://cdn/clip.mp3');
    await expect(handle.started).resolves.toEqual({ ok: true });
    expect(fetchMock).not.toHaveBeenCalled();
    let done = false;
    handle.finished.then(() => (done = true));
    FakeAudio.instances[0]!.end();
    await flush();
    expect(done).toBe(true);
  });

  it('settles finished when the stream errors after playback started', async () => {
    const handle = playCustomAudio('https://cdn/clip.mp3');
    await handle.started;
    let done = false;
    handle.finished.then(() => (done = true));
    FakeAudio.instances[0]!.failMidway();
    await flush();
    expect(done).toBe(true);
  });

  it('falls back to a blob of the fetched bytes when the URL rung fails', async () => {
    FakeAudio.behaviourFor = (src) => (src.startsWith('blob:') ? 'plays' : 'errors');
    const handle = playCustomAudio('https://cdn/clip.mp3');
    await expect(handle.started).resolves.toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(FakeAudio.instances.map((a) => a.src)).toEqual(['https://cdn/clip.mp3', 'blob:clip']);
  });

  it('falls back to Web Audio when both element rungs fail', async () => {
    FakeAudio.behaviourFor = () => 'errors';
    const handle = playCustomAudio('https://cdn/clip.mp3');
    await expect(handle.started).resolves.toEqual({ ok: true });
    expect(FakeAudioContext.created).toHaveLength(1);
    let done = false;
    handle.finished.then(() => (done = true));
    FakeAudioContext.created[0]!.lastSource!.onended?.();
    await flush();
    expect(done).toBe(true);
    expect(FakeAudioContext.created[0]!.state).toBe('closed');
  });

  it('reports every rung when nothing can play', async () => {
    FakeAudio.behaviourFor = () => 'errors';
    vi.stubGlobal('window', {});
    const handle = playCustomAudio('https://cdn/clip.mp3');
    const result = await handle.started;
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.reason).toBe('failed');
    expect(result.detail).toContain('url: SRC_NOT_SUPPORTED');
    expect(result.detail).toContain('blob: SRC_NOT_SUPPORTED');
    expect(result.detail).toContain('webaudio: no AudioContext');
    await expect(handle.finished).resolves.toBeUndefined();
  });

  it('stops at the first rung when autoplay is blocked, without downloading the clip', async () => {
    FakeAudio.behaviourFor = () => 'blocked';
    const handle = playCustomAudio('https://cdn/clip.mp3');
    const result = await handle.started;
    expect(result).toMatchObject({ ok: false, reason: 'blocked' });
    expect(fetchMock).not.toHaveBeenCalled();
    expect(clipFailureToast(result as Extract<typeof result, { ok: false }>).severity).toBe('warn');
  });

  it('does not hang on a suspended AudioContext once stopped', async () => {
    FakeAudio.behaviourFor = () => 'errors';
    FakeAudioContext.initialState = 'suspended';
    const handle = playCustomAudio('https://cdn/clip.mp3');
    let settled = false;
    handle.started.then(() => (settled = true));
    await flush();
    expect(settled).toBe(false);
    handle.stop();
    await flush();
    expect(settled).toBe(true);
    expect((await handle.started).ok).toBe(false);
  });

  it('flags an expired signed URL from the fetch status', async () => {
    FakeAudio.behaviourFor = () => 'errors';
    fetchMock.mockResolvedValue({ ok: false, status: 403, headers: { get: () => null } });
    const result = await playCustomAudio('https://cdn/clip.mp3?token=old').started;
    expect(result).toMatchObject({ ok: false, reason: 'expired' });
  });

  it('names a missing CORS header when the fetch throws', async () => {
    FakeAudio.behaviourFor = () => 'errors';
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));
    const result = await playCustomAudio('https://cdn/clip.mp3').started;
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.detail).toContain('no CORS headers');
  });

  it('fails the URL rung after a silent stall but re-arms on progress', async () => {
    FakeAudio.behaviourFor = (src) => (src.startsWith('blob:') ? 'plays' : 'stalls');
    const handle = playCustomAudio('https://cdn/clip.mp3');
    await flush();
    vi.advanceTimersByTime(5000);
    FakeAudio.instances[0]!.onprogress?.();
    vi.advanceTimersByTime(5000);
    await flush();
    expect(FakeAudio.instances).toHaveLength(1);
    vi.advanceTimersByTime(1500);
    await flush();
    await expect(handle.started).resolves.toEqual({ ok: true });
    expect(FakeAudio.instances[1]!.src).toBe('blob:clip');
  });
});
