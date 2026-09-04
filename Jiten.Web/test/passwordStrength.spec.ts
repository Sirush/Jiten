import { describe, expect, it } from 'vitest';
import { passwordStrength } from '../app/utils/passwordStrength';

// Same boundaries as validatePassword in app/pages/register.vue and the Identity options in
// Jiten.Api/Program.cs, so "Strong" cannot drift away from what the API accepts.
describe('passwordStrength', () => {
  it('scores nothing for an empty value', () => {
    expect(passwordStrength('').level).toBe('none');
    expect(passwordStrength('').score).toBe(0);
  });

  it('reads weak for a short lowercase-only password', () => {
    expect(passwordStrength('abc').level).toBe('weak');
  });

  it('is not strong at 10 characters without upper case or digit', () => {
    expect(passwordStrength('abcdefghij').level).not.toBe('strong');
  });

  it('is not strong at 8 characters even with every class present', () => {
    expect(passwordStrength('Aa1aaaaa').level).not.toBe('strong');
  });

  it('is strong at 10 characters with lower, upper and digit', () => {
    const result = passwordStrength('Aa1aaaaaaa');
    expect(result.level).toBe('strong');
    expect(result.label).toBe('Strong');
    expect(result.score).toBe(4);
  });
});
