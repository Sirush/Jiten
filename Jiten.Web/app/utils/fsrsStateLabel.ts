import { FsrsState } from '~/types/enums';

export function fsrsStateLabel(state: FsrsState): { value: string; severity: string } {
  switch (state) {
    case FsrsState.Learning:
      return { value: 'Learning', severity: 'info' };
    case FsrsState.Review:
      return { value: 'Review', severity: 'success' };
    case FsrsState.Relearning:
      return { value: 'Relearning', severity: 'warn' };
    case FsrsState.Blacklisted:
      return { value: 'Blacklisted', severity: 'danger' };
    case FsrsState.Mastered:
      return { value: 'Mastered', severity: 'success' };
    case FsrsState.Suspended:
      return { value: 'Suspended', severity: 'secondary' };
    default:
      return { value: 'New', severity: 'secondary' };
  }
}

const stateToneClasses: Record<string, string> = {
  info: 'text-blue-600 dark:text-blue-400',
  success: 'text-emerald-600 dark:text-emerald-400',
  warn: 'text-amber-600 dark:text-amber-400',
  danger: 'text-red-600 dark:text-red-400',
  secondary: 'text-surface-500 dark:text-surface-400',
};

export function fsrsStateTone(state: FsrsState): { label: string; tone: string } {
  const { value, severity } = fsrsStateLabel(state);
  return { label: value, tone: stateToneClasses[severity] ?? stateToneClasses.secondary! };
}
