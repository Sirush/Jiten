import cluster from 'node:cluster';

// Counters are per worker; site-wide figures are the sum across the reports each worker sends.
export const WORKER = `w${cluster.worker?.id ?? 0}`;

const MAX_ROUTES = 2000;
const MAX_SAMPLES = 256;
const MAX_SLOW = 50;

interface RouteStat {
  n: number;
  e: number;
  max: number;
  samples: number[];
}

export interface SlowRequest {
  at: number;
  method: string;
  path: string;
  status: number;
  ms: number;
  ip: string;
  ua: string;
}

const routes = new Map<string, RouteStat>();
let slow: SlowRequest[] = [];
let requests = 0;

/// Collapses ids so per-deck URLs aggregate into one bucket instead of thousands.
export function normalisePath(path: string) {
  return path.replace(/\d{2,}/g, ':id');
}

export function recordRequest(method: string, path: string, status: number, ms: number) {
  requests++;
  const key = `${method} ${normalisePath(path)}`;
  let stat = routes.get(key);
  if (!stat) {
    if (routes.size >= MAX_ROUTES) return;
    stat = { n: 0, e: 0, max: 0, samples: [] };
    routes.set(key, stat);
  }
  stat.n++;
  if (status >= 500) stat.e++;
  if (ms > stat.max) stat.max = ms;
  // Reservoir sampling keeps the percentile estimate unbiased on hot routes without unbounded memory.
  if (stat.samples.length < MAX_SAMPLES) stat.samples.push(ms);
  else {
    const i = Math.floor(Math.random() * stat.n);
    if (i < MAX_SAMPLES) stat.samples[i] = ms;
  }
}

export function recordSlow(entry: SlowRequest) {
  if (slow.length < MAX_SLOW) {
    slow.push(entry);
    return;
  }
  let minIndex = 0;
  for (let i = 1; i < slow.length; i++) if (slow[i]!.ms < slow[minIndex]!.ms) minIndex = i;
  if (entry.ms > slow[minIndex]!.ms) slow[minIndex] = entry;
}

function percentile(sorted: number[], p: number) {
  if (sorted.length === 0) return 0;
  return sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))]!;
}

/** Returns the window's route and slow-request figures and resets them for the next window. */
export function drainRequests() {
  const routeRows = [...routes.entries()].map(([key, s]) => {
    const space = key.indexOf(' ');
    const sorted = s.samples.sort((a, b) => a - b);
    return {
      method: key.slice(0, space),
      path: key.slice(space + 1),
      n: s.n,
      e: s.e,
      p50: Math.round(percentile(sorted, 0.5)),
      p95: Math.round(percentile(sorted, 0.95)),
      max: Math.round(s.max),
    };
  });
  const result = { requests, routes: routeRows, slow };
  routes.clear();
  slow = [];
  requests = 0;
  return result;
}
