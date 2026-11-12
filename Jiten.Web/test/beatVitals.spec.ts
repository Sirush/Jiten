import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';

type Listener = (e?: unknown) => void;

const windowListeners = new Map<string, Listener[]>();
const beacons: Blob[] = [];

function on(map: Map<string, Listener[]>) {
  return (type: string, fn: Listener) => map.set(type, [...(map.get(type) ?? []), fn]);
}

beforeAll(() => {
  vi.stubGlobal('window', {
    addEventListener: on(windowListeners),
    setTimeout: () => 1,
    clearTimeout: () => {},
    setInterval: () => 1,
  });
  vi.stubGlobal('document', { visibilityState: 'visible', referrer: '', addEventListener: () => {} });
  vi.stubGlobal('navigator', {
    language: 'en',
    webdriver: false,
    sendBeacon: (_url: string, body: Blob) => {
      beacons.push(body);
      return true;
    },
  });
  vi.stubGlobal('screen', { width: 1920, height: 1080 });
  vi.stubGlobal('location', { hostname: 'jiten.moe', pathname: '/settings', href: 'https://jiten.moe/settings' });
  vi.stubGlobal('PerformanceObserver', undefined);
});

afterAll(() => {
  vi.unstubAllGlobals();
});

describe('beat vitals', () => {
  it('carry the route of the first view, not the current one', async () => {
    const { beatStart, beatView } = await import('~/utils/beat');
    beatStart({ userId: () => undefined, apiBase: 'https://api.jiten.moe/api' });
    beatView({ path: '/decks/media/42/detail', route: '/decks/media/:id()/detail', title: 'Deck', search: '' });
    beatView({ path: '/settings', route: '/settings', title: 'Settings', search: '' });

    windowListeners.get('pagehide')?.forEach((fn) => fn());

    const events = (await Promise.all(beacons.map(async (b) => JSON.parse(await b.text()).e as Record<string, unknown>[]))).flat();
    const vitals = events.filter((e) => e.t === 'vital');
    expect(vitals.length).toBeGreaterThan(0);
    for (const vital of vitals) {
      expect(vital.r).toBe('/decks/media/:id()/detail');
      expect(vital.p).toBe('/decks/media/42/detail');
    }
  });
});
