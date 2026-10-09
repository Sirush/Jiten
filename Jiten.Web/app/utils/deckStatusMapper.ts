import { DeckStatus } from '~/types';

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
