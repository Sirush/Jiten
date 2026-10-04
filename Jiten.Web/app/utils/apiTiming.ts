export const API_TIMING_SAMPLE_RATE = 0.1;

export interface ApiTimingSample {
  n: string;
  val: number;
  at: number;
  d: Record<string, number>;
}

type ResourceEntry = Pick<
  PerformanceResourceTiming,
  'name' | 'initiatorType' | 'startTime' | 'duration' | 'requestStart' | 'responseStart' | 'responseEnd' | 'serverTiming'
> & { responseStatus?: number };

const ms = (value: number) => Math.round(Math.max(0, value) * 10) / 10;

/** Null unless the entry is an API call whose response carried the route-tagged Server-Timing header. */
export function apiTimingSample(entry: ResourceEntry, apiBase: string, timeOrigin: number): ApiTimingSample | null {
  if (entry.initiatorType !== 'fetch' && entry.initiatorType !== 'xmlhttprequest') return null;
  if (!entry.name.startsWith(apiBase) || entry.duration <= 0) return null;

  // The route template stands in for the URL, which can carry word ids, usernames and search text.
  const server = entry.serverTiming?.find((s) => s.name === 'app');
  if (!server?.description) return null;

  const d: Record<string, number> = { srv: ms(server.duration) };
  // Phase marks read as zero when the browser withholds them.
  if (entry.requestStart > 0 && entry.responseStart > 0) {
    d.wait = ms(entry.requestStart - entry.startTime);
    d.ttfb = ms(entry.responseStart - entry.requestStart);
    d.dl = ms(entry.responseEnd - entry.responseStart);
  }
  if (entry.responseStatus) d.st = entry.responseStatus;

  return { n: server.description, val: Math.round(entry.duration), at: Math.round(timeOrigin + entry.responseEnd), d };
}
