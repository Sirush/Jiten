import type { InjectionKey } from 'vue';

export const SITE_ORIGIN = 'https://jiten.moe';

export const MARKDOWN_LINK_TARGET: InjectionKey<string | undefined> = Symbol('markdownLinkTarget');

const SITE_HOSTS = new Set(['jiten.moe', 'www.jiten.moe']);
const PLAIN_PROTOCOLS = new Set(['mailto', 'tel']);

export type LinkTarget = { kind: 'internal'; to: string } | { kind: 'external'; href: string } | { kind: 'protocol'; href: string } | { kind: 'unsafe' };

function fromAbsolute(href: string): LinkTarget {
  let url: URL;
  try {
    url = new URL(href);
  } catch {
    return { kind: 'unsafe' };
  }
  if (SITE_HOSTS.has(url.hostname.toLowerCase())) return { kind: 'internal', to: `${url.pathname}${url.search}${url.hash}` };
  return { kind: 'external', href };
}

export function classifyLink(rawHref: string | undefined | null): LinkTarget {
  const href = (rawHref ?? '').trim();
  if (!href) return { kind: 'unsafe' };

  const normalised = Array.from(href)
    .filter((ch) => ch > '\u001f' && ch !== '\u007f')
    .join('')
    .replace(/\\/g, '/');

  if (normalised.startsWith('//')) return fromAbsolute(`https:${normalised}`);

  const scheme = /^([a-z][a-z0-9+.-]*):/i.exec(normalised)?.[1]?.toLowerCase();
  if (!scheme) return { kind: 'internal', to: /^[/#?]/.test(normalised) ? normalised : `/${normalised}` };
  if (scheme === 'http' || scheme === 'https') return fromAbsolute(normalised);
  if (PLAIN_PROTOCOLS.has(scheme)) return { kind: 'protocol', href: normalised };
  return { kind: 'unsafe' };
}
