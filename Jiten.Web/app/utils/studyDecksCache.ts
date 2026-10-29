import type { StudyDeckDto } from '~/types';
import { decodeJwtPayload } from '~/utils/jwt';

const KEY_PREFIX = 'jiten.studyDecks.v1.';

/** Due counts move with the clock as well as with the overview version, so an older copy is shown but always refetched. */
export const STUDY_DECKS_FRESH_MS = 5 * 60 * 1000;

/** Marks a copy saved without a server version; it is shown, but never trusted to skip a refetch. */
export const UNVERIFIED_VERSION = -1;

export const OVERVIEW_VERSION_HEADER = 'X-Overview-Version';

export interface CachedStudyDecks {
  version: number;
  savedAt: number;
  decks: StudyDeckDto[];
}

/** The JWT subject, so one browser never shows a previous account's decks. */
export function tokenSubject(token: string | null | undefined): string | null {
  const sub = decodeJwtPayload(token)?.sub;
  return typeof sub === 'string' && sub ? sub : null;
}

export function parseOverviewVersion(header: string | null): number | null {
  if (header === null || header.trim() === '') return null;
  const version = Number(header);
  return Number.isSafeInteger(version) && version >= 0 ? version : null;
}

export function parseCachedStudyDecks(raw: string | null): CachedStudyDecks | null {
  if (!raw) return null;
  try {
    const entry = JSON.parse(raw);
    if (typeof entry?.version !== 'number' || typeof entry?.savedAt !== 'number' || !Array.isArray(entry?.decks)) return null;
    return entry as CachedStudyDecks;
  } catch {
    return null;
  }
}

/** Recent and stamped by the server, so only the current version is still needed to trust it. */
export function mayStillBeFresh(entry: CachedStudyDecks, now: number): boolean {
  if (entry.version === UNVERIFIED_VERSION) return false;
  const age = now - entry.savedAt;
  return age >= 0 && age < STUDY_DECKS_FRESH_MS;
}

export function canSkipRefetch(entry: CachedStudyDecks, currentVersion: number | null, now: number): boolean {
  return currentVersion !== null && mayStillBeFresh(entry, now) && entry.version === currentVersion;
}

export function readStudyDecksCache(owner: string): CachedStudyDecks | null {
  try {
    return parseCachedStudyDecks(localStorage.getItem(KEY_PREFIX + owner));
  } catch {
    return null;
  }
}

export function writeStudyDecksCache(owner: string, entry: CachedStudyDecks) {
  try {
    localStorage.setItem(KEY_PREFIX + owner, JSON.stringify(entry));
  } catch {}
}

export function removeStudyDecksCache(owner: string) {
  try {
    localStorage.removeItem(KEY_PREFIX + owner);
  } catch {}
}

export function clearStudyDecksCache() {
  try {
    for (let i = localStorage.length - 1; i >= 0; i--) {
      const key = localStorage.key(i);
      if (key?.startsWith(KEY_PREFIX)) localStorage.removeItem(key);
    }
  } catch {}
}
