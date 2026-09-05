import { describe, expect, it } from 'vitest';
import { DESCRIBE_EXAMPLES, pickDescribeExamples } from '~/utils/describeExamples';

describe('pickDescribeExamples', () => {
  it('is deterministic for a seed and picks distinct examples', () => {
    const a = pickDescribeExamples(12345);
    const b = pickDescribeExamples(12345);
    expect(a).toEqual(b);
    expect(new Set(a).size).toBe(3);
    for (const e of a) expect(DESCRIBE_EXAMPLES).toContain(e);
  });

  it('rotates across seeds', () => {
    const seen = new Set<string>();
    for (let seed = 1; seed <= 40; seed++) for (const e of pickDescribeExamples(seed)) seen.add(e);
    expect(seen.size).toBeGreaterThan(8);
  });
});
