import { FsrsState } from '~/types';

export function getFsrsStateName(state: FsrsState): string {
  switch (state) {
    case FsrsState.New: return 'New';
    case FsrsState.Learning: return 'Learning';
    case FsrsState.Review: return 'Review';
    case FsrsState.Relearning: return 'Relearning';
    case FsrsState.Blacklisted: return 'Blacklisted';
    case FsrsState.Mastered: return 'Mastered';
    case FsrsState.Suspended: return 'Suspended';
    default: return 'Unknown';
  }
}

export function getFsrsStateSeverity(state: FsrsState): string {
  switch (state) {
    case FsrsState.New: return 'info';
    case FsrsState.Learning: return 'warn';
    case FsrsState.Review: return 'success';
    case FsrsState.Relearning: return 'warn';
    case FsrsState.Blacklisted: return 'secondary';
    case FsrsState.Mastered: return 'success';
    case FsrsState.Suspended: return 'secondary';
    default: return 'secondary';
  }
}
