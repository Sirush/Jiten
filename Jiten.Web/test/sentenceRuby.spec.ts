import { describe, expect, it } from 'vitest';
import type { SentenceFurigana } from '../app/types';
import { sentenceRubyHtml, targetWordId, visibleFurigana } from '../app/utils/sentenceRuby';

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

  it('keeps a hidden reading in the markup for hover only where asked', () => {
    const html = sentenceRubyHtml('猫と犬', -1, 0, [group(0, 1, 'ねこ', 1), group(2, 1, 'いぬ', 2)], {
      showReading: () => false,
      revealOnHover: (g) => g.wordId === 1,
    });

    expect(html).toBe('<ruby>猫<rp>(</rp><rt class="furigana-peek">ねこ</rt><rp>)</rp></ruby>と犬');
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

  it('keeps known words in exceptTarget mode', () => {
    expect(visibleFurigana(groups, 'exceptTarget', 3).map((g) => g.wordId)).toEqual([1, 2]);
  });

  it('shows nothing when off or when the sentence has no spans', () => {
    expect(visibleFurigana(groups, 'off')).toEqual([]);
    expect(visibleFurigana(null, 'all')).toEqual([]);
  });
});

describe('targetWordId', () => {
  const groups = [group(0, 2, 'きのう', 1), group(6, 1, 'た', 3)];

  it('finds the word whose ruby group sits inside the highlight', () => {
    expect(targetWordId(groups, 6, 3)).toBe(3);
  });

  it('returns nothing without a highlight or a group under it', () => {
    expect(targetWordId(groups, -1, 0)).toBeUndefined();
    expect(targetWordId(groups, 3, 2)).toBeUndefined();
    expect(targetWordId(null, 0, 2)).toBeUndefined();
  });
});

describe('sentenceRubyHtml options', () => {
  const groups = [group(0, 1, 'ねこ', 1), group(2, 1, 'いぬ', 2)];

  it('keeps a word without its reading when showReading excludes it', () => {
    const html = sentenceRubyHtml('猫と犬', -1, 0, groups, { showReading: (g) => g.wordId === 1 });

    expect(html).toBe(`${ruby('猫', 'ねこ')}と犬`);
  });

  it('colours words, with or without their reading', () => {
    const html = sentenceRubyHtml('猫と犬', -1, 0, groups, {
      showReading: (g) => g.wordId === 2,
      colourOf: (g) => (g.wordId === 1 ? '#f43f5e' : null),
    });

    expect(html).toBe(`<span style="color:#f43f5e">猫</span>と${ruby('犬', 'いぬ')}`);
  });

  it('ignores a colour that is not a hex value', () => {
    const html = sentenceRubyHtml('猫', -1, 0, [group(0, 1, 'ねこ', 1)], { colourOf: () => 'red;background:url(x)' });

    expect(html).toBe(ruby('猫', 'ねこ'));
  });
});
