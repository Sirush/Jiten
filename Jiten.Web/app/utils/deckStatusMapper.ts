import { DeckStatus } from '~/types';

export const DECK_STATUS_DISPLAY_ORDER = [DeckStatus.Completed, DeckStatus.Ongoing, DeckStatus.Paused, DeckStatus.Planning, DeckStatus.Dropped];

export function getDeckStatusText(status: DeckStatus | undefined): string {
  if (status === undefined || status === DeckStatus.None) return 'None';

  switch (status) {
    case DeckStatus.Planning:
      return 'Planning';
    case DeckStatus.Ongoing:
      return 'Ongoing';
    case DeckStatus.Paused:
      return 'Paused';
    case DeckStatus.Completed:
      return 'Completed';
    case DeckStatus.Dropped:
      return 'Dropped';
    default:
      return 'Unknown';
  }
}

export function isInProgressStatus(status: DeckStatus | undefined): boolean {
  return status === DeckStatus.Ongoing || status === DeckStatus.Paused;
}

export function getDeckStatusColourClass(status: DeckStatus | undefined): string {
  switch (status) {
    case DeckStatus.Planning:
      return 'text-gray-500 dark:text-gray-400';
    case DeckStatus.Ongoing:
      return 'text-yellow-500';
    case DeckStatus.Paused:
      return 'text-sky-600 dark:text-sky-400';
    case DeckStatus.Completed:
      return 'text-green-500';
    case DeckStatus.Dropped:
      return 'text-red-500';
    default:
      return '';
  }
}
