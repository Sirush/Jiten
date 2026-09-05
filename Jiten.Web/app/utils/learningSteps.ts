// Mirrors FsrsStepSettings on the server; the server is the authority, this only saves a round trip.
export const MAX_LEARNING_STEPS = 4;
export const MIN_STEP_MINUTES = 1;
export const MAX_STEP_MINUTES = 12 * 60;

const TOKEN = /^(\d+(?:\.\d+)?)\s*(m|min|mins|minute|minutes|h|hr|hrs|hour|hours)?$/i;

export type ParsedSteps = { ok: true; minutes: number[] } | { ok: false; error: string };

/** Parses Anki-style step text ("10m 1h"); a blank string is the "let FSRS decide" empty list. */
export function parseLearningSteps(text: string): ParsedSteps {
  const tokens = text.trim().split(/[\s,]+/).filter(Boolean);
  if (tokens.length === 0) return { ok: true, minutes: [] };
  if (tokens.length > MAX_LEARNING_STEPS) return { ok: false, error: `At most ${MAX_LEARNING_STEPS} steps.` };

  const minutes: number[] = [];
  for (const token of tokens) {
    const match = TOKEN.exec(token);
    if (!match) return { ok: false, error: `"${token}" is not a step. Use minutes or hours, like 10m or 1h.` };
    const amount = Number(match[1]);
    const unit = (match[2] ?? 'm').toLowerCase();
    const value = Math.round(unit.startsWith('h') ? amount * 60 : amount);
    if (value < MIN_STEP_MINUTES) return { ok: false, error: 'Each step must be at least 1 minute.' };
    if (value > MAX_STEP_MINUTES) return { ok: false, error: 'Each step must be under 12 hours.' };
    if (minutes.length > 0 && value <= minutes[minutes.length - 1]!) return { ok: false, error: 'Steps must increase from one to the next.' };
    minutes.push(value);
  }
  return { ok: true, minutes };
}

export function formatLearningSteps(minutes: readonly number[] | null | undefined): string {
  if (!minutes) return '';
  return minutes.map((m) => (m % 60 === 0 && m >= 60 ? `${m / 60}h` : `${m}m`)).join(' ');
}
