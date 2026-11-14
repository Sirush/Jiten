export interface WaniKaniUser {
  username: string;
  level: number;
}

export interface WaniKaniWord {
  characters: string;
  reading: string;
  srsStage: number;
  /** ISO time of the next WaniKani review; null once burned. */
  availableAt: string | null;
  startedAt: string | null;
  passedAt: string | null;
  burnedAt: string | null;
  /** Reviews WaniKani counted for the word, failed ones included. */
  reviews: number;
  /** Wrong answers across meaning and reading. */
  incorrect: number;
}

export type WaniKaniProgressPhase = 'assignments' | 'statistics' | 'subjects';

export class WaniKaniApiError extends Error {
  constructor(
    public status: number,
    message: string
  ) {
    super(message);
  }
}

export const WANIKANI_BURNED_STAGE = 9;

export const WANIKANI_STAGE_OPTIONS = [
  { label: 'Apprentice and above', value: 1 },
  { label: 'Guru and above', value: 5 },
  { label: 'Master and above', value: 7 },
  { label: 'Enlightened and above', value: 8 },
  { label: 'Burned only', value: WANIKANI_BURNED_STAGE },
] as const;

const API_BASE = 'https://api.wanikani.com/v2';
const VOCABULARY_TYPES = 'vocabulary,kana_vocabulary';
const MAX_RATE_LIMIT_RETRIES = 3;
const MAX_RATE_LIMIT_WAIT_MS = 65_000;

interface Collection<T> {
  pages: { next_url: string | null };
  total_count: number;
  data: { id: number; data: T }[];
}

interface AssignmentData {
  subject_id: number;
  srs_stage: number;
  available_at: string | null;
  started_at: string | null;
  passed_at: string | null;
  burned_at: string | null;
}

interface ReviewStatisticData {
  subject_id: number;
  meaning_correct: number;
  meaning_incorrect: number;
  reading_correct: number;
  reading_incorrect: number;
}

interface SubjectData {
  characters: string | null;
  readings?: { reading: string; primary: boolean }[];
}

export interface WaniKaniClientOptions {
  fetch?: typeof fetch;
  sleep?: (ms: number) => Promise<void>;
  now?: () => number;
}

export function createWaniKaniClient(token: string, options: WaniKaniClientOptions = {}) {
  const fetchImpl = options.fetch ?? fetch;
  const sleep = options.sleep ?? ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));
  const now = options.now ?? Date.now;

  async function get<T>(url: string): Promise<T> {
    for (let attempt = 0; ; attempt++) {
      const response = await fetchImpl(url, { headers: { Authorization: `Bearer ${token}`, 'Wanikani-Revision': '20170710' } });
      if (response.ok) return (await response.json()) as T;

      if (response.status === 429 && attempt < MAX_RATE_LIMIT_RETRIES) {
        const resetSeconds = Number(response.headers.get('RateLimit-Reset'));
        const wait = Number.isFinite(resetSeconds) && resetSeconds > 0 ? resetSeconds * 1000 - now() : MAX_RATE_LIMIT_WAIT_MS;
        await sleep(Math.min(MAX_RATE_LIMIT_WAIT_MS, Math.max(1000, wait)));
        continue;
      }

      throw new WaniKaniApiError(response.status, `WaniKani answered ${response.status}`);
    }
  }

  async function getAll<T>(url: string, onPage: (loaded: number, total: number) => void): Promise<{ id: number; data: T }[]> {
    const rows: { id: number; data: T }[] = [];
    let next: string | null = url;
    while (next) {
      const page: Collection<T> = await get<Collection<T>>(next);
      rows.push(...page.data);
      onPage(rows.length, page.total_count);
      next = page.pages.next_url;
    }
    return rows;
  }

  return {
    async getUser(): Promise<WaniKaniUser> {
      const user = await get<{ data: WaniKaniUser }>(`${API_BASE}/user`);
      return { username: user.data.username, level: user.data.level };
    },

    /** Spellings are returned as WaniKani shows them, 〜 included; the server matches on that exact text. */
    async getStudiedWords(minStage: number, onProgress: (phase: WaniKaniProgressPhase, loaded: number, total: number) => void = () => {}): Promise<WaniKaniWord[]> {
      const stages = Array.from({ length: WANIKANI_BURNED_STAGE - minStage + 1 }, (_, i) => minStage + i).join(',');
      const assignments = await getAll<AssignmentData>(
        `${API_BASE}/assignments?subject_types=${VOCABULARY_TYPES}&started=true&srs_stages=${stages}`,
        (loaded, total) => onProgress('assignments', loaded, total)
      );
      const studied = new Map(assignments.filter((a) => a.data.srs_stage >= minStage).map((a) => [a.data.subject_id, a.data]));
      if (studied.size === 0) return [];

      const statistics = new Map(
        (
          await getAll<ReviewStatisticData>(`${API_BASE}/review_statistics?subject_types=${VOCABULARY_TYPES}`, (loaded, total) =>
            onProgress('statistics', loaded, total)
          )
        ).map((s) => [s.data.subject_id, s.data])
      );

      const subjects = await getAll<SubjectData>(`${API_BASE}/subjects?types=${VOCABULARY_TYPES}`, (loaded, total) => onProgress('subjects', loaded, total));

      const words = new Map<string, WaniKaniWord>();
      for (const subject of subjects) {
        const characters = subject.data.characters;
        const assignment = studied.get(subject.id);
        if (!characters || !assignment || words.has(characters)) continue;
        const reading = subject.data.readings?.find((r) => r.primary)?.reading ?? subject.data.readings?.[0]?.reading ?? characters;
        const stats = statistics.get(subject.id);
        words.set(characters, {
          characters,
          reading,
          srsStage: assignment.srs_stage,
          availableAt: assignment.available_at ?? null,
          startedAt: assignment.started_at ?? null,
          passedAt: assignment.passed_at ?? null,
          burnedAt: assignment.burned_at ?? null,
          reviews: Math.max(stats?.meaning_correct ?? 0, stats?.reading_correct ?? 0),
          incorrect: (stats?.meaning_incorrect ?? 0) + (stats?.reading_incorrect ?? 0),
        });
      }
      return [...words.values()];
    },
  };
}
