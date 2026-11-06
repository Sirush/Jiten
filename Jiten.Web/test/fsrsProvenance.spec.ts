import { describe, expect, it } from 'vitest';
import { fsrsHistoryDetails, fsrsLossChangeText, fsrsProvenanceLine, fsrsSourceLabel } from '~/utils/fsrsProvenance';

const now = new Date(2026, 10, 6, 12);

describe('fsrsProvenanceLine', () => {
  it('names the date and review count of a fit', () => {
    expect(fsrsProvenanceLine('auto', new Date(2026, 9, 12, 9).toISOString(), 4210, now)).toBe('Updated automatically on 12 Oct from 4,210 reviews');
    expect(fsrsProvenanceLine('optimised', new Date(2025, 2, 3, 9).toISOString(), 1, now)).toBe('Optimised on 3 Mar 2025 from 1 review');
  });

  it('drops the parts a fit did not record', () => {
    expect(fsrsProvenanceLine('optimised', null, 0, now)).toBe('Optimised');
  });

  it('describes parameters that were not fitted', () => {
    expect(fsrsProvenanceLine('edited', null, 0, now)).toBe('Edited by you');
    expect(fsrsProvenanceLine('legacy', null, 0, now)).toBe('Custom parameters');
    expect(fsrsProvenanceLine('default', null, 0, now)).toBe('Default parameters');
  });
});

describe('fsrsLossChangeText', () => {
  it('reports the gain or loss', () => {
    expect(fsrsLossChangeText(0.4407, 0.4629)).toBe('Fits your reviews 4.8% better');
    expect(fsrsLossChangeText(0.5, 0.4)).toBe('Fits your reviews 25% worse');
    expect(fsrsLossChangeText(0.4, 0.4)).toBe('Fits your reviews as well');
  });

  it('is empty without both losses', () => {
    expect(fsrsLossChangeText(null, 0.4)).toBeNull();
    expect(fsrsLossChangeText(0.4, null)).toBeNull();
    expect(fsrsLossChangeText(0.4, 0)).toBeNull();
  });
});

describe('labels', () => {
  it('labels each source', () => {
    expect(fsrsSourceLabel('auto')).toBe('Automatic update');
    expect(fsrsSourceLabel('default')).toBe('Default');
  });

  it('hides an unknown review count', () => {
    expect(fsrsHistoryDetails({ reviewCount: 0, loss: null, previousLoss: null })).toEqual([]);
    expect(fsrsHistoryDetails({ reviewCount: 12000, loss: 0.4, previousLoss: 0.4 })).toEqual(['12,000 reviews', 'Fits your reviews as well']);
  });
});
