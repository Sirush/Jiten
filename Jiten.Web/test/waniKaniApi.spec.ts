import { describe, expect, it, vi } from 'vitest';
import { createWaniKaniClient, WaniKaniApiError } from '~/utils/waniKaniApi';

function json(body: unknown, init: ResponseInit = {}) {
  return new Response(JSON.stringify(body), { status: 200, ...init });
}

function collection(data: unknown[], nextUrl: string | null = null, total = data.length) {
  return json({ object: 'collection', pages: { per_page: 500, next_url: nextUrl, previous_url: null }, total_count: total, data });
}

describe('createWaniKaniClient', () => {
  it('sends the token and revision headers', async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ object: 'user', data: { username: 'koichi', level: 23 } }));
    const user = await createWaniKaniClient('secret', { fetch: fetchMock }).getUser();

    expect(user).toEqual({ username: 'koichi', level: 23 });
    expect(fetchMock).toHaveBeenCalledWith('https://api.wanikani.com/v2/user', {
      headers: { Authorization: 'Bearer secret', 'Wanikani-Revision': '20170710' },
    });
  });

  it('joins paged assignments with subjects and keeps only the requested stages', async () => {
    const fetchMock = vi.fn(async (url: string) => {
      if (url.includes('/assignments') && !url.includes('page_after_id'))
        return collection([{ id: 1, data: { subject_id: 10, srs_stage: 5, available_at: '2026-11-20T09:00:00.000000Z', started_at: '2026-10-01T08:00:00.000000Z', passed_at: '2026-10-09T08:00:00.000000Z', burned_at: null } }], 'https://api.wanikani.com/v2/assignments?page_after_id=1', 3);
      if (url.includes('/assignments'))
        return collection(
          [
            { id: 2, data: { subject_id: 11, srs_stage: 9, available_at: null, started_at: '2025-01-01T08:00:00.000000Z', passed_at: null, burned_at: '2025-09-01T08:00:00.000000Z' } },
            { id: 3, data: { subject_id: 12, srs_stage: 2, available_at: '2026-11-14T01:00:00.000000Z' } },
          ],
          null,
          3
        );
      if (url.includes('/review_statistics'))
        return collection([{ id: 90, data: { subject_id: 10, meaning_correct: 7, meaning_incorrect: 2, reading_correct: 6, reading_incorrect: 1 } }]);
      return collection([
        { id: 10, data: { characters: '大人', readings: [{ reading: 'だいにん', primary: false }, { reading: 'おとな', primary: true }] } },
        { id: 11, data: { characters: 'すぐ' } },
        { id: 12, data: { characters: '下', readings: [{ reading: 'した', primary: true }] } },
        { id: 13, data: { characters: '上', readings: [{ reading: 'うえ', primary: true }] } },
      ]);
    });
    const progress = vi.fn();

    const words = await createWaniKaniClient('secret', { fetch: fetchMock as unknown as typeof fetch }).getStudiedWords(5, progress);

    expect(words).toEqual([
      {
        characters: '大人',
        reading: 'おとな',
        srsStage: 5,
        availableAt: '2026-11-20T09:00:00.000000Z',
        startedAt: '2026-10-01T08:00:00.000000Z',
        passedAt: '2026-10-09T08:00:00.000000Z',
        burnedAt: null,
        reviews: 7,
        incorrect: 3,
      },
      {
        characters: 'すぐ',
        reading: 'すぐ',
        srsStage: 9,
        availableAt: null,
        startedAt: '2025-01-01T08:00:00.000000Z',
        passedAt: null,
        burnedAt: '2025-09-01T08:00:00.000000Z',
        reviews: 0,
        incorrect: 0,
      },
    ]);
    expect(fetchMock.mock.calls[0]![0]).toBe(
      'https://api.wanikani.com/v2/assignments?subject_types=vocabulary,kana_vocabulary&started=true&srs_stages=5,6,7,8,9'
    );
    expect(progress).toHaveBeenCalledWith('assignments', 3, 3);
    expect(progress).toHaveBeenCalledWith('subjects', 4, 4);
  });

  it('skips the subject sweep when nothing was studied', async () => {
    const fetchMock = vi.fn().mockResolvedValue(collection([]));
    const words = await createWaniKaniClient('secret', { fetch: fetchMock }).getStudiedWords(9);

    expect(words).toEqual([]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('waits for the rate limit to reset, then retries', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(json({ error: 'Rate Limit Exceeded', code: 429 }, { status: 429, headers: { 'RateLimit-Reset': '1030' } }))
      .mockResolvedValueOnce(json({ data: { username: 'koichi', level: 3 } }));
    const sleep = vi.fn().mockResolvedValue(undefined);

    await createWaniKaniClient('secret', { fetch: fetchMock, sleep, now: () => 1_000_000 }).getUser();

    expect(sleep).toHaveBeenCalledWith(30_000);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('reports a rejected token by its status', async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ error: 'Unauthorized. Nice try.', code: 401 }, { status: 401 }));

    await expect(createWaniKaniClient('wrong', { fetch: fetchMock }).getUser()).rejects.toMatchObject({ status: 401 });
    await expect(createWaniKaniClient('wrong', { fetch: fetchMock }).getUser()).rejects.toBeInstanceOf(WaniKaniApiError);
  });
});
