import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Superset of every PrimeVue style seen since boot; grows per render until all page types are visited.
const styles = new Map<string, string>();
let cachedCss = '';
let cachedHash = '';
let dirty = false;

// Shared across cluster workers
const STORE_DIR = join(tmpdir(), 'jiten-pv-styles');

const STYLE_TAG = /<style([^>]*?)data-primevue-style-id="([^"]+)"([^>]*)>([\s\S]*?)<\/style>/g;
export const STYLE_MARKER = 'data-primevue-style-id';

// Empties the inline tags in place but keeps them (with attributes) as stubs: the client style
// runtime finds an element with its style-id and skips re-inserting, so hydration neither
// duplicates styles nor reorders the cascade.
export function stripPrimevueStyles(head: (string | undefined)[]): [string, string][] {
  const extracted: [string, string][] = [];
  for (let i = 0; i < head.length; i++) {
    const chunk = head[i];
    if (!chunk || !chunk.includes(STYLE_MARKER)) continue;
    head[i] = chunk.replace(STYLE_TAG, (_match, pre, name, post, css) => {
      extracted.push([name, css]);
      return `<style${pre}${STYLE_MARKER}="${name}"${post}></style>`;
    });
  }
  return extracted;
}

// The link goes exactly where the first inline tag sat so cascade order against Tailwind and
// other head stylesheets is unchanged.
export function insertStylesheetLink(head: (string | undefined)[], href: string): void {
  const link = `<link rel="stylesheet" href="${href}">`;
  for (let i = 0; i < head.length; i++) {
    const chunk = head[i];
    const markerIdx = chunk?.indexOf(STYLE_MARKER) ?? -1;
    if (markerIdx === -1) continue;
    const styleStart = chunk!.lastIndexOf('<style', markerIdx);
    head[i] = chunk!.slice(0, styleStart) + link + chunk!.slice(styleStart);
    return;
  }
}

export function mergePrimevueStyles(entries: Iterable<readonly [string, string]>): void {
  for (const [name, css] of entries) {
    if (!styles.has(name)) {
      styles.set(name, css);
      dirty = true;
    }
  }
}

// The write must land before the HTML linking the hash goes out: the browser's CSS request can
// hit a sibling cluster worker whose only copy is the disk one.
let persisted: Promise<void> = Promise.resolve();

function rebuild(): void {
  if (!dirty && cachedHash) return;
  cachedCss = [...styles.values()].join('\n');
  cachedHash = createHash('sha1').update(cachedCss).digest('hex').slice(0, 12);
  dirty = false;
  persisted = persist(cachedHash, cachedCss);
}

export function currentPrimevueStylesheet(): { css: string; hash: string } {
  rebuild();
  return { css: cachedCss, hash: cachedHash };
}

export function primevueStylesheetPersisted(): Promise<void> {
  return persisted;
}

async function persist(hash: string, css: string): Promise<void> {
  try {
    await mkdir(STORE_DIR, { recursive: true });
    await writeFile(join(STORE_DIR, `${hash}.css`), css, 'utf8');
  } catch {
    // Disk copy only serves cross-worker lookups; the in-memory superset still answers
  }
}

export async function readPrimevueStylesheet(hash: string): Promise<string | null> {
  rebuild();
  if (hash === cachedHash) return cachedCss;
  try {
    return await readFile(join(STORE_DIR, `${hash}.css`), 'utf8');
  } catch {
    return null;
  }
}
