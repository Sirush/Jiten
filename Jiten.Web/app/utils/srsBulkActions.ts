import { FsrsState, KnownState } from '~/types/enums';

export type BulkCardAction =
  | 'neverForget-add'
  | 'neverForget-remove'
  | 'suspend-add'
  | 'suspend-remove'
  | 'blacklist-add'
  | 'blacklist-remove'
  | 'reset-schedule'
  | 'clear-lapses'
  | 'forget-add';

export interface BulkActionDef {
  label: string;
  icon: string;
  severity: string;
  run: () => void;
}

export type BulkCardStatus = 'none' | 'active' | 'mastered' | 'suspended' | 'blacklisted';

export interface BulkCardInfo {
  wordId: number;
  readingIndex: number;
  status: BulkCardStatus;
  lapses?: number;
}

export const bulkActionEligibility: Record<BulkCardAction, (card: BulkCardInfo) => boolean> = {
  'neverForget-add': (c) => c.status !== 'mastered',
  'neverForget-remove': (c) => c.status === 'mastered',
  'suspend-add': (c) => c.status !== 'suspended',
  'suspend-remove': (c) => c.status === 'suspended',
  'blacklist-add': (c) => c.status !== 'blacklisted',
  'blacklist-remove': (c) => c.status === 'blacklisted',
  'reset-schedule': (c) => c.status !== 'none',
  'clear-lapses': (c) => (c.lapses ?? 0) > 0,
  'forget-add': (c) => c.status !== 'none',
};

export function bulkStatusFromFsrsState(state: FsrsState): BulkCardStatus {
  switch (state) {
    case FsrsState.Mastered:
      return 'mastered';
    case FsrsState.Suspended:
      return 'suspended';
    case FsrsState.Blacklisted:
      return 'blacklisted';
    default:
      return 'active';
  }
}

export function bulkStatusFromKnownStates(states: KnownState[] | undefined): BulkCardStatus {
  if (!states || states.includes(KnownState.Redundant)) return 'none';
  if (states.includes(KnownState.Suspended)) return 'suspended';
  if (states.includes(KnownState.Mastered)) return 'mastered';
  if (states.includes(KnownState.Blacklisted)) return 'blacklisted';
  if (states.includes(KnownState.Young) || states.includes(KnownState.Mature) || states.includes(KnownState.Due)) return 'active';
  return 'none';
}
