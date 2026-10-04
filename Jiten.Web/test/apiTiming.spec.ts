import { describe, expect, it } from 'vitest';
import { apiTimingSample } from '~/utils/apiTiming';

const BASE = 'https://api.jiten.moe/api/';

function entry(overrides: Partial<Parameters<typeof apiTimingSample>[0]> = {}): Parameters<typeof apiTimingSample>[0] {
  return {
    name: `${BASE}vocabulary/1358280/0/info`,
    initiatorType: 'fetch',
    startTime: 1000,
    duration: 240,
    requestStart: 1120,
    responseStart: 1230,
    responseEnd: 1240,
    serverTiming: [{ name: 'app', duration: 4.2, description: 'api/vocabulary/{wordId}/{readingIndex}/info' } as PerformanceServerTiming],
    responseStatus: 200,
    ...overrides,
  };
}

describe('apiTimingSample', () => {
  it('names the sample by route template and splits the phases', () => {
    expect(apiTimingSample(entry(), BASE, 1_700_000_000_000)).toEqual({
      n: 'api/vocabulary/{wordId}/{readingIndex}/info',
      val: 240,
      at: 1_700_000_001_240,
      d: { srv: 4.2, wait: 120, ttfb: 110, dl: 10, st: 200 },
    });
  });

  it('never carries the request URL', () => {
    const sample = apiTimingSample(entry({ name: `${BASE}user/profile/someone?q=秘密` }), BASE, 0);
    expect(JSON.stringify(sample)).not.toContain('someone');
    expect(JSON.stringify(sample)).not.toContain('秘密');
  });

  it('skips responses without the route-tagged Server-Timing', () => {
    expect(apiTimingSample(entry({ serverTiming: [] }), BASE, 0)).toBeNull();
    expect(apiTimingSample(entry({ serverTiming: [{ name: 'app', duration: 3, description: '' } as PerformanceServerTiming] }), BASE, 0)).toBeNull();
  });

  it('skips other hosts, non-fetch resources and aborted requests', () => {
    expect(apiTimingSample(entry({ name: 'https://assets.jiten.moe/_nuxt/a.js' }), BASE, 0)).toBeNull();
    expect(apiTimingSample(entry({ initiatorType: 'img' }), BASE, 0)).toBeNull();
    expect(apiTimingSample(entry({ duration: 0 }), BASE, 0)).toBeNull();
  });

  it('drops phases the browser withheld', () => {
    const sample = apiTimingSample(entry({ requestStart: 0, responseStart: 0, responseStatus: 0 }), BASE, 0);
    expect(sample?.d).toEqual({ srv: 4.2 });
  });
});
