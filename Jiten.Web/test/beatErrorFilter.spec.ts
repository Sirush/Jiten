import { describe, expect, it } from 'vitest';
import { isReportableError, type ErrorReport } from '~/utils/beatErrorFilter';

const CHROME = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36';

function report(overrides: Partial<ErrorReport>): ErrorReport {
  return { name: 'TypeError', message: 'boom', stack: '', userAgent: CHROME, automated: false, ...overrides };
}

describe('isReportableError', () => {
  it('keeps app errors', () => {
    const stack = "TypeError: Cannot read properties of undefined (reading 'x')\n    at setup (https://assets.jiten.moe/_nuxt/BVnFwfOv.js:6:1430)";
    expect(isReportableError(report({ stack }))).toBe(true);
  });

  it('keeps chunk load failures without frames', () => {
    const message = 'Failed to fetch dynamically imported module: https://assets.jiten.moe/_nuxt/Bzp8htD5.js';
    expect(isReportableError(report({ message, stack: `TypeError: ${message}` }))).toBe(true);
    expect(isReportableError(report({ message: 'Importing a module script failed.', stack: '' }))).toBe(true);
  });

  it('drops extension stacks', () => {
    const stack = "TypeError: Cannot read properties of undefined (reading 'M_ID')\n    at Y (chrome-extension://eppiocemhmnlbhjplcgkofciiegomcon/executors/200.js:1:761)";
    expect(isReportableError(report({ stack }))).toBe(false);
  });

  it('drops scripts injected into the page', () => {
    const stack = 's@https://jiten.moe/vocabulary/1217700/0:708:7497\nglobal code@https://jiten.moe/vocabulary/1217700/0:1:45';
    expect(isReportableError(report({ stack }))).toBe(false);
  });

  it('drops headless browsers', () => {
    expect(isReportableError(report({ userAgent: 'Lightpanda/1.0' }))).toBe(false);
    expect(isReportableError(report({ automated: true }))).toBe(false);
  });

  it('drops known noise', () => {
    expect(isReportableError(report({ message: 'ResizeObserver loop completed with undelivered notifications.' }))).toBe(false);
    expect(isReportableError(report({ message: 'Script error.' }))).toBe(false);
    expect(isReportableError(report({ message: 'AsyncData request cancelled by deduplication' }))).toBe(false);
    expect(isReportableError(report({ message: 'Failed to connect to MetaMask' }))).toBe(false);
    expect(isReportableError(report({ name: 'AbortError', message: 'signal is aborted without reason' }))).toBe(false);
  });
});
