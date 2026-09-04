export const USERNAME_MIN = 2;
export const USERNAME_MAX = 30;

// Mirrors Jiten.Api/Helpers/UsernameValidator.cs; the server is the source of truth for this set.
const ALLOWED_PATTERN = /^[A-Za-z0-9._@+-]+$/;

export const USERNAME_ALLOWED_CHARS_MESSAGE = 'Username can only contain Latin letters, digits and the characters . _ - @ +';

export function sanitizeUsername(value: string): string {
  return value.replace(/[^A-Za-z0-9._@+-]/g, '').slice(0, USERNAME_MAX);
}

/** Returns null when valid, otherwise the message shown under the field. Pass the trimmed value. */
export function validateUsername(username: string): string | null {
  if (!username) {
    return 'Username is required';
  }
  if (username.length < USERNAME_MIN) {
    return `Username must be at least ${USERNAME_MIN} characters`;
  }
  if (username.length > USERNAME_MAX) {
    return `Username must be at most ${USERNAME_MAX} characters`;
  }
  if (!ALLOWED_PATTERN.test(username)) {
    return USERNAME_ALLOWED_CHARS_MESSAGE;
  }
  if (!/[A-Za-z0-9]/.test(username)) {
    return 'Username must contain at least one letter or digit';
  }
  return null;
}
