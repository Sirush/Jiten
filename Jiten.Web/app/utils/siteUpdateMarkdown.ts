import { SITE_ORIGIN } from '~/utils/linkTarget';

const MARKDOWN_IMAGE = /!\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g;
const INLINE_LINK_TARGET = /(\]\(\s*)(<[^>\n]*>|[^)\s]+)/g;
const REFERENCE_DEFINITION = /^( {0,3}\[[^\]\n]+\]:[ \t]*)(<[^>\n]*>|\S+)/gm;
const CODE = /(```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\n]*`)/;
const SINGLE_URL = /^https?:\/\/\S+$/i;

function absoluteUrl(target: string): string {
  if (!target || /^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith('//')) return target;
  if (target.startsWith('#')) return `${SITE_ORIGIN}/updates${target}`;
  try {
    return new URL(target, `${SITE_ORIGIN}/`).href;
  } catch {
    return target;
  }
}

function absoluteDestination(destination: string): string {
  return destination.startsWith('<') ? `<${absoluteUrl(destination.slice(1, -1))}>` : absoluteUrl(destination);
}

function absolutiseLinks(markdown: string): string {
  const rewrite = (_: string, lead: string, destination: string) => lead + absoluteDestination(destination);
  return markdown
    .split(CODE)
    .map((part, i) => (i % 2 === 1 ? part : part.replace(INLINE_LINK_TARGET, rewrite).replace(REFERENCE_DEFINITION, rewrite)))
    .join('');
}

/** Discord embeds bare image URLs but renders no Markdown images, and masked links only work with absolute URLs. */
export function toDiscordMarkdown(title: string, body: string): string {
  return `**${title}**\n\n${absolutiseLinks(body.replace(MARKDOWN_IMAGE, '$1'))}`;
}

export interface TextSplice {
  text: string;
  cursor: number;
}

function padding(existingNewlines: number): string {
  return '\n'.repeat(Math.max(0, 2 - existingNewlines));
}

/** Replaces the [start, end) selection with `insert` as its own paragraph, so an image never renders inline with text. */
export function spliceAsParagraph(text: string, start: number, end: number, insert: string): TextSplice {
  let before = text.slice(0, start);
  let after = text.slice(end);
  if (before.length > 0 && !before.endsWith('\n')) {
    before = before.replace(/[ \t]+$/, '');
    after = after.replace(/^[ \t]+/, '');
  }
  const lead = before.length === 0 ? '' : padding(/\n{0,2}$/.exec(before)![0].length);
  const trail = after.length === 0 ? '' : padding(/^\n{0,2}/.exec(after)![0].length);
  return { text: before + lead + insert + trail + after, cursor: before.length + lead.length + insert.length };
}

/** Wraps the [start, end) selection as `[selection](url)` when a single URL is pasted over it; null means paste normally. */
export function linkifySelection(text: string, start: number, end: number, pasted: string): TextSplice | null {
  const url = pasted.trim();
  const selection = text.slice(start, end);
  if (!selection.trim() || selection.includes('\n\n') || !SINGLE_URL.test(url)) return null;

  const destination = /[()<>]/.test(url) ? `<${url.replace(/[<>]/g, encodeURIComponent)}>` : url;
  const link = `[${selection}](${destination})`;
  return { text: text.slice(0, start) + link + text.slice(end), cursor: start + link.length };
}
