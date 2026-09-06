type Payload = Record<string, unknown>;

const ENDPOINT = '/api/jr';
const FLUSH_MS = 1500;
const IDLE_MS = 60_000;
const MAX_ERRORS_PER_VIEW = 10;

let enabled = false;
let queue: Payload[] = [];
let flushTimer: number | undefined;

let viewId = '';
let viewPath = '';
let viewErrors = 0;
let firstView = true;
let vitalsSent = false;

let engagedSince: number | null = null;
let activeMs = 0;
let lastInput = 0;

let userIdSource: () => string | undefined = () => undefined;

function id(): string {
  const bytes = new Uint8Array(6);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

function push(item: Payload): void {
  if (!enabled) return;
  queue.push({ ...item, at: Date.now() });
  if (queue.length >= 20) flush();
  else if (flushTimer === undefined) flushTimer = window.setTimeout(flush, FLUSH_MS);
}

function flush(): void {
  if (flushTimer !== undefined) {
    window.clearTimeout(flushTimer);
    flushTimer = undefined;
  }
  if (queue.length === 0) return;
  const now = Date.now();
  const events = queue.map(({ at, ...rest }) => ({ ...rest, dt: Math.max(0, now - Number(at)) }));
  queue = [];
  const body = JSON.stringify({ s: location.hostname, e: events });
  try {
    if (navigator.sendBeacon && navigator.sendBeacon(ENDPOINT, new Blob([body], { type: 'text/plain' }))) return;
  } catch {}
  fetch(ENDPOINT, { method: 'POST', body, keepalive: true, headers: { 'content-type': 'text/plain' }, credentials: 'omit' }).catch(() => {});
}

function closeEngagement(now: number): void {
  if (engagedSince !== null) {
    activeMs += now - engagedSince;
    engagedSince = null;
  }
}

function onInput(): void {
  const now = Date.now();
  lastInput = now;
  if (engagedSince === null && document.visibilityState === 'visible') engagedSince = now;
}

function idleCheck(): void {
  const now = Date.now();
  if (engagedSince !== null && now - lastInput > IDLE_MS) closeEngagement(now);
}

/** Flushes the active time of the current view as an increment; the server sums increments per view. */
function sendLeave(): void {
  if (!viewId) return;
  const now = Date.now();
  closeEngagement(now);
  if (activeMs > 0) {
    push({ t: 'leave', v: viewId, p: viewPath, val: Math.round(activeMs) });
    activeMs = 0;
  }
  if (document.visibilityState === 'visible') engagedSince = now;
}

function sendVitals(): void {
  if (vitalsSent || !firstViewId) return;
  vitalsSent = true;
  const v = firstViewId;
  const nav = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined;
  if (nav && nav.responseStart > 0) push({ t: 'vital', n: 'TTFB', val: Math.round(nav.responseStart), v, p: firstViewPath });
  if (vitals.fcp !== null) push({ t: 'vital', n: 'FCP', val: Math.round(vitals.fcp), v, p: firstViewPath });
  if (vitals.lcp !== null) push({ t: 'vital', n: 'LCP', val: Math.round(vitals.lcp), v, p: firstViewPath });
  push({ t: 'vital', n: 'CLS', val: Math.round(vitals.cls * 1000) / 1000, v, p: firstViewPath });
  if (vitals.inp !== null) push({ t: 'vital', n: 'INP', val: Math.round(vitals.inp), v, p: firstViewPath });
}

const vitals = { lcp: null as number | null, cls: 0, inp: null as number | null, fcp: null as number | null };
let firstViewId = '';
let firstViewPath = '';

function observeVitals(): void {
  if (typeof PerformanceObserver === 'undefined') return;
  const observe = (type: string, cb: (entries: PerformanceEntry[]) => void, extra?: PerformanceObserverInit) => {
    try {
      const po = new PerformanceObserver((list) => cb(list.getEntries()));
      po.observe({ type, buffered: true, ...extra } as PerformanceObserverInit);
    } catch {}
  };
  observe('largest-contentful-paint', (entries) => {
    const last = entries[entries.length - 1];
    if (last) vitals.lcp = last.startTime;
  });
  observe('paint', (entries) => {
    for (const e of entries) if (e.name === 'first-contentful-paint') vitals.fcp = e.startTime;
  });
  observe('layout-shift', (entries) => {
    for (const e of entries as (PerformanceEntry & { hadRecentInput?: boolean; value?: number })[]) {
      if (!e.hadRecentInput) vitals.cls += e.value ?? 0;
    }
  });
  observe(
    'event',
    (entries) => {
      for (const e of entries as (PerformanceEntry & { interactionId?: number })[]) {
        if (e.interactionId) vitals.inp = Math.max(vitals.inp ?? 0, e.duration);
      }
    },
    { durationThreshold: 40 } as PerformanceObserverInit
  );
}

export interface ViewInfo {
  path: string;
  route: string;
  title: string;
  search: string;
}

export function beatView(info: ViewInfo): void {
  if (!enabled) return;
  sendLeave();
  viewId = id();
  viewPath = info.path;
  viewErrors = 0;
  const now = Date.now();
  lastInput = now;
  engagedSince = document.visibilityState === 'visible' ? now : null;
  activeMs = 0;
  const item: Payload = {
    t: 'view',
    v: viewId,
    p: info.path,
    r: info.route,
    ti: info.title,
    q: info.search,
    sc: `${screen.width}x${screen.height}`,
    l: navigator.language,
  };
  const u = userIdSource();
  if (u) item.u = u;
  if (firstView) {
    firstView = false;
    firstViewId = viewId;
    firstViewPath = info.path;
    item.ref = document.referrer || '';
    if (navigator.webdriver) item.d = { hl: '1' };
  }
  push(item);
}

export function beatEvent(name: string, data?: Record<string, string | number | boolean>): void {
  if (!enabled) return;
  const item: Payload = { t: 'event', n: name, p: viewPath, v: viewId };
  if (data) item.d = data;
  const u = userIdSource();
  if (u) item.u = u;
  push(item);
}

export function beatError(source: 'vue' | 'window' | 'promise', error: unknown): void {
  if (!enabled || viewErrors >= MAX_ERRORS_PER_VIEW) return;
  viewErrors++;
  const err = error as { message?: unknown; stack?: unknown } | null;
  const message = String(err?.message ?? error ?? 'Unknown error').slice(0, 200);
  const stack = typeof err?.stack === 'string' ? err.stack.slice(0, 1500) : '';
  const u = userIdSource();
  const item: Payload = { t: 'error', n: message, p: viewPath, v: viewId, d: { stack, source } };
  if (u) item.u = u;
  push(item);
}

export function beatStart(options: { userId: () => string | undefined }): void {
  if (enabled) return;
  enabled = true;
  userIdSource = options.userId;
  observeVitals();
  const passive = { passive: true, capture: true } as AddEventListenerOptions;
  for (const type of ['pointerdown', 'keydown', 'scroll', 'touchstart', 'mousemove']) window.addEventListener(type, onInput, passive);
  window.setInterval(idleCheck, 5000);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') {
      sendVitals();
      sendLeave();
      flush();
    } else {
      onInput();
    }
  });
  window.addEventListener('pagehide', () => {
    sendVitals();
    sendLeave();
    flush();
  });
  window.addEventListener('error', (e) => beatError('window', e.error ?? e.message));
  window.addEventListener('unhandledrejection', (e) => beatError('promise', e.reason));
}
