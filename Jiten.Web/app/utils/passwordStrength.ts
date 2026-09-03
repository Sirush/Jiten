export type PasswordStrengthLevel = 'none' | 'weak' | 'medium' | 'strong';

export interface PasswordStrength {
  level: PasswordStrengthLevel;
  label: string;
  /** Rules satisfied out of four: length 10+, lowercase, uppercase, digit. */
  score: number;
}

export const PASSWORD_MIN_LENGTH = 10;
export const PASSWORD_REQUIREMENTS = 'At least 10 characters including upper, lower, digit';

export function passwordStrength(value: string): PasswordStrength {
  if (!value) {
    return { level: 'none', label: '', score: 0 };
  }

  const rules = [value.length >= PASSWORD_MIN_LENGTH, /[a-z]/.test(value), /[A-Z]/.test(value), /\d/.test(value)];
  const score = rules.filter(Boolean).length;

  if (score === rules.length) {
    return { level: 'strong', label: 'Strong', score };
  }
  if (score >= 2) {
    return { level: 'medium', label: 'Medium', score };
  }
  return { level: 'weak', label: 'Weak', score };
}
