import type { SentenceFurigana, SentenceFuriganaMode } from '~/types';
import { escapeHtml } from '~/utils/sanitiseHtml';

const highlightClass = 'text-primary-500 dark:text-primary-500 font-bold';

export function visibleFurigana(
  furigana: SentenceFurigana[] | null | undefined,
  mode: SentenceFuriganaMode,
  hiddenWordId?: number
): SentenceFurigana[] {
  if (!furigana || mode === 'off') return [];
  return furigana.filter((g) => (mode === 'all' || !g.known) && g.wordId !== hiddenWordId);
}

export function sentenceRubyHtml(text: string, wordPosition: number, wordLength: number, furigana: SentenceFurigana[]): string {
  const groups = [...furigana].sort((a, b) => a.position - b.position);

  const render = (start: number, end: number) => {
    let html = '';
    let cursor = start;
    for (const g of groups) {
      if (g.position < cursor || g.position + g.length > end) continue;
      html += escapeHtml(text.slice(cursor, g.position));
      html += `<ruby>${escapeHtml(text.slice(g.position, g.position + g.length))}<rp>(</rp><rt>${escapeHtml(g.reading)}</rt><rp>)</rp></ruby>`;
      cursor = g.position + g.length;
    }
    return html + escapeHtml(text.slice(cursor, end));
  };

  const hasWord = wordLength > 0 && wordPosition >= 0 && wordPosition + wordLength <= text.length;
  if (!hasWord) return render(0, text.length);

  return (
    render(0, wordPosition) +
    `<span class="${highlightClass}">${render(wordPosition, wordPosition + wordLength)}</span>` +
    render(wordPosition + wordLength, text.length)
  );
}
