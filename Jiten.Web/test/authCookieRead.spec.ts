import { describe, it, expect, afterEach } from 'vitest';
import { readCookie } from '~/utils/cookieMonitor';

function setDocumentCookie(value: string | null) {
  if (value === null) {
    delete (globalThis as { document?: unknown }).document;
    return;
  }
  (globalThis as { document?: unknown }).document = { cookie: value };
}

afterEach(() => setDocumentCookie(null));

describe('readCookie', () => {
  it('returns null when there is no document', () => {
    expect(readCookie('token')).toBeNull();
  });

  it('reads a cookie regardless of position', () => {
    setDocumentCookie('a=1; token=abc; refreshToken=def');
    expect(readCookie('token')).toBe('abc');
    expect(readCookie('refreshToken')).toBe('def');
  });

  it('reads the first cookie in the jar', () => {
    setDocumentCookie('token=abc; a=1');
    expect(readCookie('token')).toBe('abc');
  });

  it('does not confuse a name that is a suffix of another', () => {
    setDocumentCookie('refreshToken=def; token=abc');
    expect(readCookie('token')).toBe('abc');
  });

  it('still yields a value when the same name is present twice', () => {
    setDocumentCookie('token=host-only; token=domain-wide');
    expect(readCookie('token')).toBe('domain-wide');
  });

  it('decodes percent-encoded values', () => {
    setDocumentCookie('refreshToken=aGVsbG8%3D');
    expect(readCookie('refreshToken')).toBe('aGVsbG8=');
  });

  it('returns the raw value when it is not valid percent-encoding', () => {
    setDocumentCookie('token=100%pure');
    expect(readCookie('token')).toBe('100%pure');
  });

  it('returns null for a missing or empty cookie', () => {
    setDocumentCookie('a=1; token=');
    expect(readCookie('token')).toBeNull();
  });
});
