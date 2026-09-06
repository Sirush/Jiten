import { constants, monitorEventLoopDelay, PerformanceObserver } from 'node:perf_hooks';
import v8 from 'node:v8';

const REPORT_MS = Number(process.env.PERF_REPORT_MS) || 60_000;

const toMs = (nanoseconds: number) => Math.round(nanoseconds / 1e6);

export default defineNitroPlugin((nitroApp) => {
  // The prerenderer instantiates the nitro app inside `nuxt build`; sampling there measures
  // the build's event loop and adds allocation pressure to an already memory-tight process.
  if (import.meta.prerender || process.env.PERF_MONITOR === 'off') return;

  const loopDelay = monitorEventLoopDelay({ resolution: 20 });
  loopDelay.enable();

  let gcCount = 0;
  let gcTotalMs = 0;
  let gcMaxMs = 0;
  let gcMaxMajorMs = 0;

  const observer = new PerformanceObserver((list) => {
    for (const entry of list.getEntries()) {
      const kind = (entry as { detail?: { kind?: number } }).detail?.kind;
      gcCount++;
      gcTotalMs += entry.duration;
      gcMaxMs = Math.max(gcMaxMs, entry.duration);
      if (kind === constants.NODE_PERFORMANCE_GC_MAJOR) {
        gcMaxMajorMs = Math.max(gcMaxMajorMs, entry.duration);
      }
    }
  });
  observer.observe({ entryTypes: ['gc'] });

  const timer = setInterval(() => {
    const heap = v8.getHeapStatistics();
    const mem = process.memoryUsage();
    const { requests, routes, slow } = drainRequests();

    sendToKami('/ingest/nitro', {
      worker: WORKER,
      at: Date.now(),
      periodMs: REPORT_MS,
      pid: process.pid,
      uptimeS: Math.round(process.uptime()),
      requests,
      routes,
      slow,
      loop: { p50: toMs(loopDelay.percentile(50)), p99: toMs(loopDelay.percentile(99)), max: toMs(loopDelay.max) },
      gc: { n: gcCount, ms: Math.round(gcTotalMs), max: Math.round(gcMaxMs), major: Math.round(gcMaxMajorMs) },
      mem: { heap: heap.used_heap_size, limit: heap.heap_size_limit, rss: mem.rss, external: mem.external },
    });

    loopDelay.reset();
    gcCount = 0;
    gcTotalMs = 0;
    gcMaxMs = 0;
    gcMaxMajorMs = 0;
  }, REPORT_MS);

  timer.unref();

  nitroApp.hooks.hook('close', () => {
    clearInterval(timer);
    observer.disconnect();
    loopDelay.disable();
  });
});
