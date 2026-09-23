import { describe, expect, it } from 'vitest';
import { headwordSizeClass } from '../app/utils/headwordSizeClass';

describe('headwordSizeClass', () => {
  it('keeps short words at the default size everywhere', () => {
    expect(headwordSizeClass('猫[ねこ]')).toBe('text-3xl');
    expect(headwordSizeClass('食[た]べ物[もの]')).toBe('text-3xl');
  });

  it('measures the rendered text, not the ruby markup', () => {
    expect(headwordSizeClass('一[いち]二[に]三[さん]四[し]五[ご]六[ろく]七[しち]八[はち]')).toBe('text-3xl');
  });

  it('steps down by length, more so on phones', () => {
    expect(headwordSizeClass('人の恋路を邪魔する')).toBe('text-2xl md:text-3xl');
    expect(headwordSizeClass('人[ひと]の恋路[こいじ]を邪魔[じゃま]する奴[やつ]は馬[うま]に蹴[け]られて死[し]んじまえ')).toBe('text-xl md:text-2xl');
  });

  it('starts one step smaller in compact mode', () => {
    expect(headwordSizeClass('猫', true)).toBe('text-2xl');
    expect(headwordSizeClass('人の恋路を邪魔する奴は馬に蹴られて死んじまえ', true)).toBe('text-lg md:text-xl');
  });
});
