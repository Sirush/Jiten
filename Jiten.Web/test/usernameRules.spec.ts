import { describe, expect, it } from 'vitest';
import { USERNAME_ALLOWED_CHARS_MESSAGE, sanitizeUsername, validateUsername } from '../app/utils/usernameRules';

// Same cases as Register_InvalidUsername_Returns400_AndCreatesNoUser in Jiten.Tests/Integration/AccountTests.cs,
// so the client cannot accept a name the server will refuse.
describe('validateUsername', () => {
  it('rejects katakana with the allowed-characters message', () => {
    expect(validateUsername('タナカ')).toBe(USERNAME_ALLOWED_CHARS_MESSAGE);
  });

  it('rejects Cyrillic look-alikes, spaces and #', () => {
    expect(validateUsername('ааа')).toBe(USERNAME_ALLOWED_CHARS_MESSAGE);
    expect(validateUsername('user name')).toBe(USERNAME_ALLOWED_CHARS_MESSAGE);
    expect(validateUsername('user#name')).toBe(USERNAME_ALLOWED_CHARS_MESSAGE);
  });

  it('accepts . @ + and email-style names', () => {
    expect(validateUsername('tony@aol.com')).toBeNull();
    expect(validateUsername('a.b+c')).toBeNull();
    expect(validateUsername('Benjamin_')).toBeNull();
  });

  it('enforces the 2 to 30 character range', () => {
    expect(validateUsername('a')).toMatch(/at least 2/);
    expect(validateUsername('ab')).toBeNull();
    expect(validateUsername('a'.repeat(30))).toBeNull();
    expect(validateUsername('a'.repeat(31))).toMatch(/at most 30/);
  });

  it('requires at least one letter or digit', () => {
    expect(validateUsername('___')).toMatch(/letter or digit/);
    expect(validateUsername('...')).toMatch(/letter or digit/);
  });

  it('requires a value', () => {
    expect(validateUsername('')).toMatch(/required/);
  });
});

describe('sanitizeUsername', () => {
  it('keeps the server-allowed punctuation and drops the rest', () => {
    expect(sanitizeUsername('tony.smith+jp@mail')).toBe('tony.smith+jp@mail');
    expect(sanitizeUsername('タナカ tanaka#1')).toBe('tanaka1');
  });

  it('caps at 30 characters', () => {
    expect(sanitizeUsername('a'.repeat(40))).toHaveLength(30);
  });
});
