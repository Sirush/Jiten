import { KnownState } from '~/types';

/** Text colour for an inline word, from the states the API resolved for the viewer. */
export function knownStateTextClass(states: KnownState[] | undefined): string {
  if (!states || states.length === 0) return '';
  if (states.includes(KnownState.Blacklisted) || states.includes(KnownState.Suspended)) return 'text-surface-400 dark:text-surface-500';
  if (states.includes(KnownState.Redundant)) return 'text-sky-600 dark:text-sky-400';
  if (states.includes(KnownState.Mastered) || states.includes(KnownState.Mature)) return 'text-emerald-600 dark:text-emerald-400';
  if (states.includes(KnownState.Due)) return 'text-orange-600 dark:text-orange-400';
  if (states.includes(KnownState.Young)) return 'text-amber-600 dark:text-amber-400';
  return 'text-rose-600 dark:text-rose-400';
}

export function isUnknownState(states: KnownState[] | undefined): boolean {
  return !states || states.length === 0 || (states.length === 1 && states[0] === KnownState.New);
}
