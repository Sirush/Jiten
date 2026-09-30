import type { SentenceFurigana, SentenceFuriganaMode } from '~/types';
import { escapeHtml } from '~/utils/sanitiseHtml';

const highlightClass = 'text-primary-500 dark:text-primary-500 font-bold';
const unknownMarkClass = 'bg-blue-100 dark:bg-blue-900/60 rounded-sm';
const unknownLinkClass = 'hover:bg-blue-200 dark:hover:bg-blue-800 hover:underline focus-visible:outline-2 focus-visible:outline-primary-500';

export function visibleFurigana(
  furigana: SentenceFurigana[] | null | undefined,
  mode: SentenceFuriganaMode,
  hiddenWordId?: number
): SentenceFurigana[] {
  if (!furigana || mode === 'off') return [];
  return furigana.filter((g) => (mode !== 'unknown' || !g.known) && g.wordId !== hiddenWordId);
}

/** The word under the sentence's highlight, so every occurrence of it can lose its reading. */
export function targetWordId(furigana: SentenceFurigana[] | null | undefined, wordPosition: number, wordLength: number): number | undefined {
  if (wordLength <= 0) return undefined;
  return furigana?.find((g) => g.position < wordPosition + wordLength && g.position + g.length > wordPosition)?.wordId;
}

export interface SentenceRubyOptions {
  /** Groups left out keep their colour but lose the reading; defaults to showing every group. */
  showReading?: (group: SentenceFurigana) => boolean;
  /** For a group whose reading is hidden: render it anyway, invisible until the word is hovered. */
  revealOnHover?: (group: SentenceFurigana) => boolean;
  /** A #rrggbb colour for the group's word, or null for the ordinary text colour. */
  colourOf?: (group: SentenceFurigana) => string | null;
  /** Spans to highlight as words the reader doesn't know yet. */
  unknownSpans?: { position: number; length: number; wordId?: number; readingIndex?: number }[];
}

const hexColour = /^#[0-9a-fA-F]{6}$/;

export function sentenceRubyHtml(text: string, wordPosition: number, wordLength: number, furigana: SentenceFurigana[], options: SentenceRubyOptions = {}): string {
  const groups = [...furigana].sort((a, b) => a.position - b.position);

  const render = (start: number, end: number) => {
    let html = '';
    let cursor = start;
    for (const g of groups) {
      if (g.position < cursor || g.position + g.length > end) continue;
      html += escapeHtml(text.slice(cursor, g.position));
      const base = escapeHtml(text.slice(g.position, g.position + g.length));
      const shown = options.showReading?.(g) !== false;
      const rt = shown ? '<rt>' : options.revealOnHover?.(g) ? '<rt class="furigana-peek">' : null;
      const word = rt ? `<ruby>${base}<rp>(</rp>${rt}${escapeHtml(g.reading)}</rt><rp>)</rp></ruby>` : base;
      const colour = options.colourOf?.(g);
      html += colour && hexColour.test(colour) ? `<span style="color:${colour}">${word}</span>` : word;
      cursor = g.position + g.length;
    }
    return html + escapeHtml(text.slice(cursor, end));
  };

  const spans = [...(options.unknownSpans ?? [])].sort((a, b) => a.position - b.position);
  const renderMarked = (start: number, end: number) => {
    let html = '';
    let cursor = start;
    for (const s of spans) {
      if (s.position < cursor || s.position + s.length > end) continue;
      const inner = render(s.position, s.position + s.length);
      const marked =
        s.wordId != null && Number.isInteger(s.wordId) && Number.isInteger(s.readingIndex)
          ? `<a href="/vocabulary/${s.wordId}/${s.readingIndex}" target="_blank" rel="noopener" class="${unknownMarkClass} ${unknownLinkClass}">${inner}</a>`
          : `<span class="${unknownMarkClass}">${inner}</span>`;
      html += render(cursor, s.position) + marked;
      cursor = s.position + s.length;
    }
    return html + render(cursor, end);
  };

  const hasWord = wordLength > 0 && wordPosition >= 0 && wordPosition + wordLength <= text.length;
  if (!hasWord) return renderMarked(0, text.length);

  return (
    renderMarked(0, wordPosition) +
    `<span class="${highlightClass}">${render(wordPosition, wordPosition + wordLength)}</span>` +
    renderMarked(wordPosition + wordLength, text.length)
  );
}
