import { describe, expect, it } from 'vitest';
import { buildFirstTouch, firstTouchAge, referrerHost, signupEventProps } from '~/utils/firstTouch';

describe('referrerHost', () => {
  it('keeps the external host only', () => {
    expect(referrerHost('https://www.YouTube.com/watch?v=abc', 'jiten.moe')).toBe('www.youtube.com');
  });

  it('treats internal links and direct visits as empty', () => {
    expect(referrerHost('https://jiten.moe/decks/media', 'jiten.moe')).toBe('');
    expect(referrerHost('', 'jiten.moe')).toBe('');
    expect(referrerHost('not a url', 'jiten.moe')).toBe('');
  });
});

describe('firstTouchAge', () => {
  const now = new Date('2026-10-03T15:00:00Z');

  it('buckets by whole UTC days', () => {
    expect(firstTouchAge('2026-10-03', now)).toBe('0');
    expect(firstTouchAge('2026-10-02', now)).toBe('1-6');
    expect(firstTouchAge('2026-09-27', now)).toBe('1-6');
    expect(firstTouchAge('2026-09-26', now)).toBe('7-29');
    expect(firstTouchAge('2026-09-04', now)).toBe('7-29');
    expect(firstTouchAge('2026-09-03', now)).toBe('30+');
  });

  it('ignores a corrupt date', () => {
    expect(firstTouchAge('yesterday', now)).toBeUndefined();
  });
});

describe('buildFirstTouch', () => {
  it('captures route, referrer host, utm source and day', () => {
    const touch = buildFirstTouch(
      '/decks/media/:id()/detail',
      'https://www.google.com/',
      'jiten.moe',
      '?utm_source=creator&x=1',
      new Date('2026-10-03T23:59:00Z')
    );
    expect(touch).toEqual({ route: '/decks/media/:id()/detail', ref: 'www.google.com', utm: 'creator', day: '2026-10-03' });
  });
});

describe('signupEventProps', () => {
  it('keeps an empty referrer so direct visits are countable', () => {
    expect(signupEventProps({ route: '/', referrer: '', age: '0' })).toEqual({ ft_route: '/', ft_ref: '', ft_age: '0' });
  });

  it('adds the credited prompt', () => {
    expect(signupEventProps({ prompt: 'download_dialog' })).toEqual({ prompt: 'download_dialog' });
  });

  it('is empty without a source', () => {
    expect(signupEventProps(undefined)).toEqual({});
  });
});
