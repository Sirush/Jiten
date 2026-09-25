import { describe, expect, it } from 'vitest';
import type { SentenceFurigana } from '../app/types';
import { sentenceRubyHtml, visibleFurigana } from '../app/utils/sentenceRuby';

const group = (position: number, length: number, reading: string, wordId: number, known = false): SentenceFurigana => ({
  position,
  length,
  reading,
  wordId,
  known,
});

const ruby = (base: string, reading: string) => `<ruby>${base}<rp>(</rp><rt>${reading}</rt><rp>)</rp></ruby>`;
const highlight = (html: string) => `<span class="text-primary-500 dark:text-primary-500 font-bold">${html}</span>`;

describe('sentenceRubyHtml', () => {
  it('places ruby inside and outside the highlighted word', () => {
    const html = sentenceRubyHtml('昨日は猫を見た', 3, 1, [group(0, 2, 'きのう', 1), group(3, 1, 'ねこ', 2), group(5, 1, 'み', 3)]);

    expect(html).toBe(`${ruby('昨日', 'きのう')}は${highlight(ruby('猫', 'ねこ'))}を${ruby('見', 'み')}た`);
  });

  it('leaves a group crossing the highlight edge bare instead of splitting it', () => {
    const html = sentenceRubyHtml('今日は', 1, 2, [group(0, 2, 'きょう', 1)]);

    expect(html).toBe(`今${highlight('日は')}`);
  });

  it('escapes sentence text and readings', () => {
    const html = sentenceRubyHtml('<b>猫', -1, 0, [group(3, 1, '<i>', 1)]);

    expect(html).toBe(`&lt;b&gt;${ruby('猫', '&lt;i&gt;')}`);
  });

  it('renders without a highlight when the word span is missing', () => {
    expect(sentenceRubyHtml('猫', 0, 0, [group(0, 1, 'ねこ', 1)])).toBe(ruby('猫', 'ねこ'));
  });
});

describe('visibleFurigana', () => {
  const groups = [group(0, 1, 'ねこ', 1, true), group(2, 1, 'いぬ', 2, false), group(4, 1, 'とり', 3, false)];

  it('keeps unknown words only in unknown mode', () => {
    expect(visibleFurigana(groups, 'unknown').map((g) => g.wordId)).toEqual([2, 3]);
  });

  it('keeps every word in all mode but still hides the tested word', () => {
    expect(visibleFurigana(groups, 'all', 3).map((g) => g.wordId)).toEqual([1, 2]);
  });

  it('shows nothing when off or when the sentence has no spans', () => {
    expect(visibleFurigana(groups, 'off')).toEqual([]);
    expect(visibleFurigana(null, 'all')).toEqual([]);
  });
});
