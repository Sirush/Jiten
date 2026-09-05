import { describe, expect, it } from 'vitest';
import { formatLearningSteps, parseLearningSteps } from '../app/utils/learningSteps';

describe('parseLearningSteps', () => {
  it('reads minutes and hours in Anki syntax', () => {
    expect(parseLearningSteps('10m 1h')).toEqual({ ok: true, minutes: [10, 60] });
    expect(parseLearningSteps('5, 30, 2h')).toEqual({ ok: true, minutes: [5, 30, 120] });
    expect(parseLearningSteps('1.5h')).toEqual({ ok: true, minutes: [90] });
  });

  it('treats blank as the empty list', () => {
    expect(parseLearningSteps('   ')).toEqual({ ok: true, minutes: [] });
  });

  it('rejects what the server would reject', () => {
    expect(parseLearningSteps('1d').ok).toBe(false);
    expect(parseLearningSteps('30m 10m').ok).toBe(false);
    expect(parseLearningSteps('10m 10m').ok).toBe(false);
    expect(parseLearningSteps('0m').ok).toBe(false);
    expect(parseLearningSteps('13h').ok).toBe(false);
    expect(parseLearningSteps('1 2 3 4 5').ok).toBe(false);
  });
});

describe('formatLearningSteps', () => {
  it('round-trips through parse', () => {
    expect(formatLearningSteps([10, 60, 90])).toBe('10m 1h 90m');
    expect(formatLearningSteps([])).toBe('');
    expect(formatLearningSteps(null)).toBe('');
    expect(parseLearningSteps(formatLearningSteps([10, 60, 90]))).toEqual({ ok: true, minutes: [10, 60, 90] });
  });
});
