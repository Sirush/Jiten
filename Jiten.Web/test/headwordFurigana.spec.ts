import { describe, expect, it } from 'vitest';
import { KnownState } from '../app/types';
import { convertToRubyWithFurigana, headwordFuriganaVisible } from '../app/utils/convertToRuby';
import { stripRubyMarkup } from '../app/utils/stripRubyMarkup';
import { isKnownForFurigana, wordStateColourKey } from '../app/utils/wordState';

describe('headwordFuriganaVisible', () => {
  it('always and never ignore the word state', () => {
    expect(headwordFuriganaVisible('shown', [KnownState.Mature])).toBe(true);
    expect(headwordFuriganaVisible('hidden', [KnownState.New])).toBe(false);
  });

  it('hides the reading only for known words in unknown mode', () => {
    expect(headwordFuriganaVisible('unknown', [KnownState.Mature])).toBe(false);
    expect(headwordFuriganaVisible('unknown', [KnownState.New])).toBe(true);
    expect(headwordFuriganaVisible('unknown', [])).toBe(true);
  });

  it('shows the reading when the state is not available', () => {
    expect(headwordFuriganaVisible('unknown', undefined)).toBe(true);
    expect(headwordFuriganaVisible('unknown', null)).toBe(true);
  });
});

describe('convertToRubyWithFurigana', () => {
  it('drops the reading but keeps the ruby base when hidden', () => {
    expect(convertToRubyWithFurigana('猫[ねこ]', false)).toBe('<ruby lang="ja">猫</ruby>');
  });

  it('drops the reading of an iteration mark when hidden', () => {
    expect(convertToRubyWithFurigana('死[し] 屍[し] 累々[るいるい]', false)).toBe('<ruby lang="ja">死</ruby> <ruby lang="ja">屍</ruby> <ruby lang="ja">累々</ruby>');
  });

  it('keeps supplementary-plane kanji inside the ruby base', () => {
    expect(convertToRubyWithFurigana('\u{30EDE}\u{30EDE}麺[ビャンビャンめん]', true)).toBe('<ruby lang="ja">\u{30EDE}\u{30EDE}麺<rp>(</rp><rt>ビャンビャンめん</rt><rp>)</rp></ruby>');
  });
});

describe('stripRubyMarkup', () => {
  it('reads supplementary-plane kanji runs as their reading', () => {
    expect(stripRubyMarkup('\u{30EDE}\u{30EDE}麺[ビャンビャンめん]')).toBe('ビャンビャンめん');
  });
});

describe('isKnownForFurigana', () => {
  it('matches the API rule for which states count as known', () => {
    expect(isKnownForFurigana([KnownState.Young])).toBe(true);
    expect(isKnownForFurigana([KnownState.Redundant])).toBe(true);
    expect(isKnownForFurigana([KnownState.Blacklisted])).toBe(true);
    expect(isKnownForFurigana([KnownState.Due])).toBe(false);
    expect(isKnownForFurigana([KnownState.Suspended])).toBe(false);
  });
});

describe('wordStateColourKey', () => {
  it('picks the most telling state', () => {
    expect(wordStateColourKey(undefined)).toBe('new');
    expect(wordStateColourKey([KnownState.Mature, KnownState.Suspended])).toBe('ignored');
    expect(wordStateColourKey([KnownState.Young, KnownState.Due])).toBe('due');
    expect(wordStateColourKey([KnownState.Mastered])).toBe('mature');
  });
});

describe('convertToRubyWithFurigana hover reveal', () => {
  it('renders a hidden reading invisible until hovered', () => {
    expect(convertToRubyWithFurigana('食[た]べる', false, true)).toBe('<ruby lang="ja">食<rp>(</rp><rt class="furigana-peek">た</rt><rp>)</rp></ruby>べる');
  });

  it('leaves a shown reading as a plain rt', () => {
    expect(convertToRubyWithFurigana('食[た]べる', true, true)).toBe('<ruby lang="ja">食<rp>(</rp><rt>た</rt><rp>)</rp></ruby>べる');
  });
});
