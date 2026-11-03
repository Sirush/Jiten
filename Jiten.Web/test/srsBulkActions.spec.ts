import { describe, expect, it } from 'vitest';
import { FsrsState, KnownState } from '~/types/enums';
import { type BulkCardInfo, bulkActionEligibility, bulkStatusFromFsrsState, bulkStatusFromKnownStates } from '~/utils/srsBulkActions';

const card = (status: BulkCardInfo['status'], lapses?: number): BulkCardInfo => ({ wordId: 1, readingIndex: 0, status, lapses });

describe('bulkStatusFromKnownStates', () => {
  it('treats words without their own card as none', () => {
    expect(bulkStatusFromKnownStates(undefined)).toBe('none');
    expect(bulkStatusFromKnownStates([KnownState.New])).toBe('none');
    expect(bulkStatusFromKnownStates([KnownState.Redundant, KnownState.Mature])).toBe('none');
  });

  it('maps held and active states', () => {
    expect(bulkStatusFromKnownStates([KnownState.Suspended])).toBe('suspended');
    expect(bulkStatusFromKnownStates([KnownState.Mastered])).toBe('mastered');
    expect(bulkStatusFromKnownStates([KnownState.Blacklisted])).toBe('blacklisted');
    expect(bulkStatusFromKnownStates([KnownState.Young])).toBe('active');
    expect(bulkStatusFromKnownStates([KnownState.Due, KnownState.Mature])).toBe('active');
  });
});

describe('bulkStatusFromFsrsState', () => {
  it('maps every scheduled state to active', () => {
    for (const s of [FsrsState.New, FsrsState.Learning, FsrsState.Review, FsrsState.Relearning]) expect(bulkStatusFromFsrsState(s)).toBe('active');
    expect(bulkStatusFromFsrsState(FsrsState.Suspended)).toBe('suspended');
  });
});

describe('bulkActionEligibility', () => {
  it('offers the release actions only for held cards', () => {
    expect(bulkActionEligibility['suspend-remove'](card('suspended'))).toBe(true);
    expect(bulkActionEligibility['suspend-remove'](card('active'))).toBe(false);
    expect(bulkActionEligibility['neverForget-remove'](card('mastered'))).toBe(true);
    expect(bulkActionEligibility['blacklist-remove'](card('blacklisted'))).toBe(true);
  });

  it('skips words without a card for card-only actions', () => {
    expect(bulkActionEligibility['reset-schedule'](card('none'))).toBe(false);
    expect(bulkActionEligibility['forget-add'](card('none'))).toBe(false);
    expect(bulkActionEligibility['neverForget-add'](card('none'))).toBe(true);
  });

  it('clears lapses only when there are some', () => {
    expect(bulkActionEligibility['clear-lapses'](card('active', 3))).toBe(true);
    expect(bulkActionEligibility['clear-lapses'](card('active', 0))).toBe(false);
    expect(bulkActionEligibility['clear-lapses'](card('active'))).toBe(false);
  });
});
