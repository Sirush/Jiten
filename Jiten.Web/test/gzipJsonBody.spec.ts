import { afterEach, describe, expect, it, vi } from 'vitest';
import { gzipJsonBody } from '~/utils/gzipJsonBody';

async function gunzip(blob: Blob): Promise<string> {
  const stream = blob.stream().pipeThrough(new DecompressionStream('gzip'));
  return await new Response(stream).text();
}

describe('gzipJsonBody', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('gzips the JSON and marks the encoding', async () => {
    const payload = { cards: [{ Card: { Word: '食べる' } }], overwrite: false };

    const { body, headers } = await gzipJsonBody(payload);

    expect(headers).toEqual({ 'Content-Type': 'application/json', 'Content-Encoding': 'gzip' });
    expect(body).toBeInstanceOf(Blob);
    expect(JSON.parse(await gunzip(body as Blob))).toEqual(payload);
  });

  it('shrinks repetitive review-log payloads', async () => {
    const log = { Rating: 3, ReviewDateTime: '2026-10-21T04:43:34.248Z', ReviewDuration: 4200, State: 2 };
    const payload = { cards: Array.from({ length: 500 }, (_, i) => ({ Card: { Word: `語${i}` }, ReviewLogs: Array(20).fill(log) })) };

    const { body } = await gzipJsonBody(payload);

    expect((body as Blob).size).toBeLessThan(JSON.stringify(payload).length / 10);
  });

  it('sends plain JSON when CompressionStream is unavailable', async () => {
    vi.stubGlobal('CompressionStream', undefined);

    const { body, headers } = await gzipJsonBody({ a: 1 });

    expect(headers).toEqual({ 'Content-Type': 'application/json' });
    expect(body).toBe('{"a":1}');
  });
});
