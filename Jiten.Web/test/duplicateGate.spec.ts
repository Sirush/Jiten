import { describe, expect, it } from 'vitest';
import { hasExactDuplicate, isDuplicateGateSatisfied } from '../app/utils/duplicateGate';

const exact = { isExactMatch: true };
const fuzzy = { isExactMatch: false };

describe('hasExactDuplicate', () => {
  it('fires on an exact deck or an exact request', () => {
    expect(hasExactDuplicate([exact], [])).toBe(true);
    expect(hasExactDuplicate([], [exact])).toBe(true);
    expect(hasExactDuplicate([fuzzy], [fuzzy, exact])).toBe(true);
  });

  it('ignores fuzzy matches and empty results', () => {
    expect(hasExactDuplicate([fuzzy], [fuzzy])).toBe(false);
    expect(hasExactDuplicate([], [])).toBe(false);
  });
});

describe('isDuplicateGateSatisfied', () => {
  it('blocks an exact match until acknowledged', () => {
    expect(isDuplicateGateSatisfied(true, false)).toBe(false);
    expect(isDuplicateGateSatisfied(true, true)).toBe(true);
  });

  it('never blocks without an exact match', () => {
    expect(isDuplicateGateSatisfied(false, false)).toBe(true);
  });
});
