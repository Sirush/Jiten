const IGNORED_MESSAGES = [
  /ResizeObserver loop/,
  /^Script error\.?$/,
  /AsyncData request cancelled/,
  /The user aborted a request/,
  /can't access dead object/,
  /MetaMask|ethereum/i,
  /__firefox__/,
];

const EXTENSION_SCHEMES = /(chrome|moz|safari|safari-web|ms-browser)-extension:\/\/|webkit-masked-url:/;
const STACK_FRAME = /:\d+:\d+\)?\s*$/m;

export interface ErrorReport {
  name: string;
  message: string;
  stack: string;
  userAgent: string;
  automated: boolean;
}

export function isReportableError(report: ErrorReport): boolean {
  if (report.automated || /Lightpanda|HeadlessChrome/.test(report.userAgent)) return false;
  if (report.name === 'AbortError') return false;
  if (IGNORED_MESSAGES.some((re) => re.test(report.message))) return false;
  if (EXTENSION_SCHEMES.test(report.stack)) return false;
  // Frames that never touch the app bundle come from scripts injected into the page (extensions, in-app browsers).
  if (STACK_FRAME.test(report.stack) && !report.stack.includes('/_nuxt/')) return false;
  return true;
}
