const FIRST_TOUCH_KEY = 'jiten.firstTouch';
const LAST_PROMPT_KEY = 'jiten.lastPrompt';

export interface FirstTouch {
  route: string;
  ref: string;
  utm: string;
  /** UTC date of the first visit, YYYY-MM-DD. */
  day: string;
}

export type FirstTouchAge = '0' | '1-6' | '7-29' | '30+';

export interface SignupSource {
  route?: string;
  referrer?: string;
  utm?: string;
  age?: FirstTouchAge;
  prompt?: string;
}

/** Empty for direct visits and for links from jiten.moe itself. */
export function referrerHost(referrer: string, ownHost: string): string {
  if (!referrer) return '';
  try {
    const host = new URL(referrer).hostname.toLowerCase();
    return host === ownHost.toLowerCase() ? '' : host;
  } catch {
    return '';
  }
}

export function utcDay(date: Date): string {
  return date.toISOString().slice(0, 10);
}

export function firstTouchAge(day: string, now: Date): FirstTouchAge | undefined {
  const first = Date.parse(`${day}T00:00:00Z`);
  if (Number.isNaN(first)) return undefined;
  const days = Math.floor((Date.parse(`${utcDay(now)}T00:00:00Z`) - first) / 86_400_000);
  if (days <= 0) return '0';
  if (days < 7) return '1-6';
  if (days < 30) return '7-29';
  return '30+';
}

export function buildFirstTouch(route: string, referrer: string, ownHost: string, search: string, now: Date): FirstTouch {
  const utm = new URLSearchParams(search).get('utm_source') ?? '';
  return { route: route.slice(0, 120), ref: referrerHost(referrer, ownHost), utm: utm.slice(0, 50), day: utcDay(now) };
}

export function readFirstTouch(): FirstTouch | null {
  try {
    const raw = localStorage.getItem(FIRST_TOUCH_KEY);
    return raw ? (JSON.parse(raw) as FirstTouch) : null;
  } catch {
    return null;
  }
}

/** Keeps the very first landing only; later visits never overwrite it. */
export function recordFirstTouch(route: string): void {
  if (import.meta.server) return;
  try {
    if (localStorage.getItem(FIRST_TOUCH_KEY)) return;
    localStorage.setItem(FIRST_TOUCH_KEY, JSON.stringify(buildFirstTouch(route, document.referrer, location.hostname, location.search, new Date())));
  } catch {}
}

/** The prompt credited for a sign-up is the last one clicked in this tab session. */
export function markGuestPrompt(prompt: string): void {
  if (import.meta.server) return;
  try {
    sessionStorage.setItem(LAST_PROMPT_KEY, prompt);
  } catch {}
}

function readLastPrompt(): string | undefined {
  try {
    return sessionStorage.getItem(LAST_PROMPT_KEY) ?? undefined;
  } catch {
    return undefined;
  }
}

export function currentSignupSource(): SignupSource | undefined {
  if (import.meta.server) return undefined;
  const touch = readFirstTouch();
  const prompt = readLastPrompt();
  if (!touch && !prompt) return undefined;
  return {
    route: touch?.route || undefined,
    referrer: touch ? touch.ref : undefined,
    utm: touch?.utm || undefined,
    age: touch ? firstTouchAge(touch.day, new Date()) : undefined,
    prompt,
  };
}

/** Event properties for signup_completed; ft_ref stays empty for direct visits so they can be counted. */
export function signupEventProps(source: SignupSource | undefined): Record<string, string> {
  if (!source) return {};
  const props: Record<string, string> = {};
  if (source.route !== undefined) props.ft_route = source.route;
  if (source.referrer !== undefined) props.ft_ref = source.referrer;
  if (source.utm !== undefined) props.ft_utm = source.utm;
  if (source.age !== undefined) props.ft_age = source.age;
  if (source.prompt !== undefined) props.prompt = source.prompt;
  return props;
}
