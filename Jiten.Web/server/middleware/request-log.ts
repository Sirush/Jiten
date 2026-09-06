const SLOW_MS = Number(process.env.PERF_SLOW_MS) || 1000;

const SKIP_PREFIXES = ['/_nuxt', '/__nuxt', '/healthz', '/_scripts', '/_fonts'];
const SKIP_EXTENSIONS = /\.(?:js|mjs|css|map|png|jpe?g|svg|webp|ico|woff2?|ttf|wasm|txt|xml)$/i;

// Feeds the per-route counters that server/plugins/perf-monitor.ts reports to Kami every minute.
export default defineEventHandler((event) => {
  if (import.meta.prerender || process.env.PERF_MONITOR === 'off') return;

  const path = (event.path || '').split('?')[0] ?? '';
  if (SKIP_PREFIXES.some((p) => path.startsWith(p)) || SKIP_EXTENSIONS.test(path)) return;

  const req = event.node.req;
  const res = event.node.res;
  const method = req.method ?? 'GET';
  const start = performance.now();

  res.once('finish', () => {
    const duration = performance.now() - start;
    recordRequest(method, path, res.statusCode, duration);
    if (duration < SLOW_MS) return;
    const forwarded = String(req.headers['x-forwarded-for'] ?? '')
      .split(',')[0]
      ?.trim();
    recordSlow({
      at: Date.now(),
      method,
      path,
      status: res.statusCode,
      ms: Math.round(duration),
      ip: String(req.headers['cf-connecting-ip'] ?? '') || forwarded || req.socket?.remoteAddress || '',
      ua: String(req.headers['user-agent'] ?? '').slice(0, 200),
    });
  });
});
