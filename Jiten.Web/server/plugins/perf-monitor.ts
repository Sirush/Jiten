import cluster from 'node:cluster';
import { constants, monitorEventLoopDelay, PerformanceObserver } from 'node:perf_hooks';
import v8 from 'node:v8';

const SAMPLE_MS = Number(process.env.PERF_SAMPLE_MS) || 10_000;

// Every worker samples its own loop and heap; without the id the interleaved lines can't be
// attributed, and one sick worker is indistinguishable from a fleet-wide problem.
const WORKER = `w${cluster.worker?.id ?? 0}`;

const toMs = (nanoseconds: number) => Math.round(nanoseconds / 1e6);
const toMb = (bytes: number) => Math.round(bytes / 1024 / 1024);

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

    console.log(
      `[perf ${WORKER}] lag p50=${toMs(loopDelay.percentile(50))}ms p99=${toMs(loopDelay.percentile(99))}ms max=${toMs(loopDelay.max)}ms`
      + ` | gc n=${gcCount} total=${Math.round(gcTotalMs)}ms max=${Math.round(gcMaxMs)}ms major=${Math.round(gcMaxMajorMs)}ms`
      + ` | heap ${toMb(heap.used_heap_size)}/${toMb(heap.heap_size_limit)}MB rss=${toMb(mem.rss)}MB ext=${toMb(mem.external)}MB`,
    );

    loopDelay.reset();
    gcCount = 0;
    gcTotalMs = 0;
    gcMaxMs = 0;
    gcMaxMajorMs = 0;
  }, SAMPLE_MS);

  timer.unref();

  nitroApp.hooks.hook('close', () => {
    clearInterval(timer);
    observer.disconnect();
    loopDelay.disable();
  });
});
