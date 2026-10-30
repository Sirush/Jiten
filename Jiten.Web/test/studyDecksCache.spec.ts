import { describe, expect, it } from 'vitest';
import {
  STUDY_DECKS_FRESH_MS,
  UNVERIFIED_VERSION,
  canSkipRefetch,
  parseCachedStudyDecks,
  parseOverviewVersion,
  tokenSubject,
  type CachedStudyDecks,
} from '~/utils/studyDecksCache';

function jwt(claims: object) {
  const payload = btoa(JSON.stringify(claims)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `header.${payload}.signature`;
}

describe('tokenSubject', () => {
  it('reads the sub claim from an unpadded base64url payload', () => {
    expect(tokenSubject(jwt({ sub: 'f54fb6c3-ae4d-40f9-8258-3e0589828fe2', exp: 1 }))).toBe('f54fb6c3-ae4d-40f9-8258-3e0589828fe2');
  });

  it('gives null for a missing or malformed token', () => {
    expect(tokenSubject(null)).toBeNull();
    expect(tokenSubject('not-a-jwt')).toBeNull();
    expect(tokenSubject(jwt({ exp: 1 }))).toBeNull();
  });
});

describe('parseOverviewVersion', () => {
  it('accepts non-negative integers only', () => {
    expect(parseOverviewVersion('0')).toBe(0);
    expect(parseOverviewVersion('42')).toBe(42);
    expect(parseOverviewVersion(null)).toBeNull();
    expect(parseOverviewVersion('')).toBeNull();
    expect(parseOverviewVersion('-1')).toBeNull();
    expect(parseOverviewVersion('1.5')).toBeNull();
    expect(parseOverviewVersion('abc')).toBeNull();
  });
});

describe('parseCachedStudyDecks', () => {
  it('rejects anything that is not a full entry', () => {
    expect(parseCachedStudyDecks(null)).toBeNull();
    expect(parseCachedStudyDecks('{broken')).toBeNull();
    expect(parseCachedStudyDecks(JSON.stringify({ version: 1, savedAt: 1 }))).toBeNull();
    expect(parseCachedStudyDecks(JSON.stringify({ version: '1', savedAt: 1, decks: [] }))).toBeNull();
  });

  it('round-trips a valid entry', () => {
    const entry = { version: 3, savedAt: 1000, decks: [] };
    expect(parseCachedStudyDecks(JSON.stringify(entry))).toEqual(entry);
  });
});

describe('canSkipRefetch', () => {
  const now = 10_000_000;
  const entry = (overrides: Partial<CachedStudyDecks> = {}): CachedStudyDecks => ({ version: 7, savedAt: now - 1000, decks: [], ...overrides });

  it('skips only for a recent copy whose version still matches', () => {
    expect(canSkipRefetch(entry(), 7, now)).toBe(true);
  });

  it('refetches when the server version moved', () => {
    expect(canSkipRefetch(entry(), 8, now)).toBe(false);
  });

  it('refetches once the copy is too old, even at the same version', () => {
    expect(canSkipRefetch(entry({ savedAt: now - STUDY_DECKS_FRESH_MS }), 7, now)).toBe(false);
  });

  it('refetches a copy saved in the future, as after a clock change', () => {
    expect(canSkipRefetch(entry({ savedAt: now + 1000 }), 7, now)).toBe(false);
  });

  it('never trusts an unverified copy or an unknown server version', () => {
    expect(canSkipRefetch(entry({ version: UNVERIFIED_VERSION }), UNVERIFIED_VERSION, now)).toBe(false);
    expect(canSkipRefetch(entry(), null, now)).toBe(false);
  });
});
