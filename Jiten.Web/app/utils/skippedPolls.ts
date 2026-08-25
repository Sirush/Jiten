const STORAGE_KEY = 'jiten-skipped-polls';
const MAX_STORED = 500;

export function parseSkippedPollIds(raw: string | null): number[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((id): id is number => Number.isInteger(id));
  } catch {
    return [];
  }
}

export function appendSkippedPollId(ids: number[], id: number): number[] {
  const next = ids.filter((existing) => existing !== id);
  next.push(id);
  return next.slice(-MAX_STORED);
}

export function readSkippedPollIds(): number[] {
  try {
    return parseSkippedPollIds(localStorage.getItem(STORAGE_KEY));
  } catch {
    return [];
  }
}

export function recordSkippedPollId(id: number): number[] {
  const next = appendSkippedPollId(readSkippedPollIds(), id);
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
  } catch {
  }
  return next;
}
