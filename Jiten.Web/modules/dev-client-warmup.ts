import { relative } from 'node:path';
import { defineNuxtModule, useLogger } from 'nuxt/kit';

const WARMED_ROUTE = '/';
/** Nuxt's own warm-up runs for at most this long after the server build, on the same transform pipeline. */
const NUXT_WARMUP_BUDGET_MS = 20_000;
/** Browsers open at most six HTTP/1.1 connections per origin. */
const CONCURRENCY = 6;
const MODULE_SCRIPT = /<script[^>]+type="module"[^>]+src="([^"]+)"/g;
const MODULE_PRELOAD = /<link[^>]+rel="modulepreload"[^>]+href="([^"]+)"/g;
const STYLESHEET_LINK = /<link[^>]+rel="stylesheet"[^>]+href="([^"]+)"/g;
const STATIC_IMPORT = /(?:^|[\n;])\s*(?:import\s*(?:[^"'()]*?\bfrom\s*)?|export\s[^"';]*?\bfrom\s*)["']([^"']+)["']/g;
const DYNAMIC_IMPORT = /\bimport\(\s*["']([^"']+)["']/g;

interface WarmupRequest {
  url: string;
  accept: string;
  followsDynamicImports: boolean;
}

/** Requests what a browser would on a first visit: the page, its linked stylesheets and the static import graph of its module scripts. */
async function warmFirstLoad(origin: string, pageModuleUrl: string | undefined, isClosed: () => boolean) {
  const seen = new Set<string>();
  const queue: WarmupRequest[] = [];
  const enqueue = (specifier: string, from: string, accept: string, followsDynamicImports = false) => {
    const url = new URL(specifier, from);
    if (url.origin !== origin || seen.has(url.href)) return;
    seen.add(url.href);
    queue.push({ url: url.href, accept, followsDynamicImports });
  };

  /** `sec-fetch-mode` keeps Nuxt from counting this as a browser navigation, which would cancel its own warm-up. */
  const page = await fetch(origin + WARMED_ROUTE, { headers: { accept: 'text/html', 'sec-fetch-mode': 'cors' } });
  const html = await page.text();
  for (const [, src] of html.matchAll(MODULE_SCRIPT)) enqueue(src!, page.url, '*/*', true);
  for (const [, href] of html.matchAll(MODULE_PRELOAD)) enqueue(href!, page.url, '*/*');
  for (const [, href] of html.matchAll(STYLESHEET_LINK)) enqueue(href!, page.url, 'text/css');
  if (pageModuleUrl) enqueue(pageModuleUrl, page.url, '*/*', true);

  const visit = async ({ url, accept, followsDynamicImports }: WarmupRequest) => {
    const response = await fetch(url, { headers: { accept } });
    const body = await response.text();
    if (!response.headers.get('content-type')?.includes('javascript')) return;
    for (const [, specifier] of body.matchAll(STATIC_IMPORT)) enqueue(specifier!, url, '*/*');
    if (followsDynamicImports) for (const [, specifier] of body.matchAll(DYNAMIC_IMPORT)) enqueue(specifier!, url, '*/*');
  };

  let active = 0;
  await new Promise<void>((done) => {
    const pump = () => {
      if (isClosed() || (!queue.length && !active)) return done();
      while (active < CONCURRENCY && queue.length) {
        active++;
        visit(queue.shift()!)
          .catch(() => {})
          .finally(() => {
            active--;
            pump();
          });
      }
    };
    pump();
  });
  return seen.size;
}

/** Nuxt's own warm-up spends its whole time budget on server modules, so the browser's first load still compiles every client module on demand. */
export default defineNuxtModule({
  meta: { name: 'dev-client-warmup' },
  setup(_options, nuxt) {
    if (!nuxt.options.dev || nuxt.options.test) return;

    const logger = useLogger('dev-client-warmup');
    let closed = false;
    let pageModuleUrl: string | undefined;
    let timer: NodeJS.Timeout | undefined;
    nuxt.hook('close', () => {
      closed = true;
      clearTimeout(timer);
    });
    nuxt.hook('pages:resolved', (pages) => {
      const file = pages.find((page) => page.path === WARMED_ROUTE)?.file;
      if (file) pageModuleUrl = `${nuxt.options.app.buildAssetsDir}${relative(nuxt.options.srcDir, file).replaceAll('\\', '/')}`;
    });
    nuxt.hook('nitro:init', (nitro) => {
      nitro.hooks.hookOnce('compiled', () => {
        timer = setTimeout(async () => {
          const origin = new URL(nuxt.options.devServer.url).origin;
          const start = performance.now();
          try {
            const count = await warmFirstLoad(origin, pageModuleUrl, () => closed);
            logger.debug(`Warmed ${count} first-load requests for ${WARMED_ROUTE} in ${Math.round(performance.now() - start)}ms`);
          } catch (error) {
            logger.debug('First-load warm-up failed:', error);
          }
        }, NUXT_WARMUP_BUDGET_MS);
        timer.unref();
      });
    });
  },
});
