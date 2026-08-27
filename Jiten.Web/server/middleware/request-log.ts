import cluster from 'node:cluster';

const SLOW_MS = Number(process.env.PERF_SLOW_MS) || 1000;
const AGGREGATE_MS = Number(process.env.PERF_AGGREGATE_MS) || 60_000;
const MAX_KEYS = 2000;

const SKIP_PREFIXES = ['/_nuxt', '/__nuxt', '/healthz', '/_scripts', '/_fonts'];
const SKIP_EXTENSIONS = /\.(?:js|mjs|css|map|png|jpe?g|svg|webp|ico|woff2?|ttf|wasm|txt|xml)$/i;

// Counters are per worker, so a cluster emits one set of lines per worker and site-wide
// totals are the sum across them, not any single line.
const WORKER = `w${cluster.worker?.id ?? 0}`;

const pathCounts = new Map<string, number>();
const agentCounts = new Map<string, number>();
let requestCount = 0;
let aggregateTimer: NodeJS.Timeout | undefined;

/// Collapses ids so per-deck URLs aggregate into one bucket instead of thousands.
function normalisePath(path: string) {
  return path.replace(/\d{2,}/g, ':id');
}

function bump(counts: Map<string, number>, key: string) {
  const current = counts.get(key);
  if (current === undefined && counts.size >= MAX_KEYS) return;
  counts.set(key, (current ?? 0) + 1);
}

function top(counts: Map<string, number>, n: number) {
  return [...counts.entries()]
    .sort((a, b) => b[1] - a[1])
    .slice(0, n)
    .map(([key, count]) => `${count}x ${key}`)
    .join(' | ');
}

function startAggregate() {
  aggregateTimer = setInterval(() => {
    if (requestCount > 0) {
      console.log(`[reqs ${WORKER}] ${requestCount} in ${AGGREGATE_MS / 1000}s`);
      console.log(`[reqs ${WORKER}] paths: ${top(pathCounts, 8)}`);
      console.log(`[reqs ${WORKER}] agents: ${top(agentCounts, 8)}`);
    }
    pathCounts.clear();
    agentCounts.clear();
    requestCount = 0;
  }, AGGREGATE_MS);
  aggregateTimer.unref();
}

export default defineEventHandler((event) => {
  if (import.meta.prerender || process.env.PERF_MONITOR === 'off') return;

  const path = (event.path || '').split('?')[0] ?? '';
  if (SKIP_PREFIXES.some((p) => path.startsWith(p)) || SKIP_EXTENSIONS.test(path)) return;

  if (!aggregateTimer) startAggregate();

  const req = event.node.req;
  const res = event.node.res;
  const userAgent = String(req.headers['user-agent'] ?? 'none').slice(0, 100);
  const forwarded = String(req.headers['x-forwarded-for'] ?? '')
    .split(',')[0]
    ?.trim();
  const ip = forwarded || req.socket?.remoteAddress || 'unknown';
  const start = performance.now();

  requestCount++;
  bump(pathCounts, normalisePath(path));
  bump(agentCounts, userAgent);

  // Only the tail is logged per-request; at ~25 req/s a line per request would be unreadable.
  res.once('finish', () => {
    const duration = performance.now() - start;
    if (duration < SLOW_MS) return;
    console.log(`[slow ${WORKER}] ${duration.toFixed(0)}ms ${req.method} ${path} status=${res.statusCode} ip=${ip} ua="${userAgent}"`);
  });
});
