import { describe, expect, it } from 'vitest';
import { parseCheckoutAttribution, resolvePlusEntry } from '~/utils/checkoutSource';

describe('resolvePlusEntry', () => {
  it('prefers an explicit src tag', () => {
    expect(resolvePlusEntry({ src: 'gate:smart-deck', backRoute: '/srs/decks', stored: '/', referrerHost: 'www.google.com' })).toBe('gate:smart-deck');
  });

  it('credits the in-app page the visitor came from', () => {
    expect(resolvePlusEntry({ backRoute: '/settings/subscription', stored: '/', referrerHost: '' })).toBe('/settings/subscription');
  });

  it('keeps the stored entry across a sign-in or a return from Stripe', () => {
    expect(resolvePlusEntry({ backRoute: '/register', stored: '/srs/decks', referrerHost: '' })).toBe('/srs/decks');
    expect(resolvePlusEntry({ backRoute: '/jiten-plus', stored: '/srs/decks', referrerHost: '' })).toBe('/srs/decks');
    expect(resolvePlusEntry({ stored: '/srs/decks', referrerHost: 'checkout.stripe.com' })).toBe('/srs/decks');
  });

  it('falls back to the external referrer, then direct', () => {
    expect(resolvePlusEntry({ referrerHost: 'www.reddit.com' })).toBe('ref:www.reddit.com');
    expect(resolvePlusEntry({ backRoute: '/login', referrerHost: '' })).toBe('direct');
  });

  it('ignores a blank src', () => {
    expect(resolvePlusEntry({ src: '  ', backRoute: '/', referrerHost: '' })).toBe('/');
  });
});

describe('parseCheckoutAttribution', () => {
  it('reads plan, source and section', () => {
    const raw = JSON.stringify({ plan: 'yearly', source: '/srs/decks', section: 'marketing' });
    expect(parseCheckoutAttribution(raw)).toEqual({ plan: 'yearly', source: '/srs/decks', section: 'marketing' });
  });

  it('drops unknown and non-string fields', () => {
    expect(parseCheckoutAttribution(JSON.stringify({ plan: 'monthly', source: 3, other: 'x' }))).toEqual({ plan: 'monthly' });
  });

  it('is undefined for missing or corrupt values', () => {
    expect(parseCheckoutAttribution(null)).toBeUndefined();
    expect(parseCheckoutAttribution('{oops')).toBeUndefined();
    expect(parseCheckoutAttribution('null')).toBeUndefined();
    expect(parseCheckoutAttribution('{}')).toBeUndefined();
  });
});
