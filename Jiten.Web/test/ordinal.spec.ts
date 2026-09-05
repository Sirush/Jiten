import { describe, expect, it } from 'vitest';
import { ordinal } from '../app/utils/ordinal';

describe('ordinal', () => {
  it('handles the teens and the units', () => {
    expect([1, 2, 3, 4, 11, 12, 13, 21, 22, 23, 100, 101, 111, 112].map(ordinal)).toEqual([
      '1st', '2nd', '3rd', '4th', '11th', '12th', '13th', '21st', '22nd', '23rd', '100th', '101st', '111th', '112th',
    ]);
  });
});
