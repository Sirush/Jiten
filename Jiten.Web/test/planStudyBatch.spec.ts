import { describe, expect, it } from 'vitest';
import { DeckDownloadType, StudyDeckType } from '../app/types/enums';
import { buildPlanStudyBatch, isDeckStudied, planDeckIds, planStepsToAdd } from '../app/utils/planStudyBatch';

const steps = [{ deckId: 11 }, { deckId: 22 }, { deckId: 33 }];

describe('buildPlanStudyBatch', () => {
  it('sends the plan threshold as an occurrence-count filter', () => {
    const batch = buildPlanStudyBatch(steps, 12, false, false);
    expect(batch.downloadType).toBe(DeckDownloadType.OccurrenceCount);
    expect(batch.minOccurrences).toBe(12);
    expect(batch.deckIds).toEqual([11, 22, 33]);
    expect(batch.deactivateOthers).toBe(false);
    expect(batch.addToTop).toBe(false);
  });

  it('keeps plan order and carries the option flags', () => {
    const batch = buildPlanStudyBatch([{ deckId: 33 }, { deckId: 11 }], 4, true, true);
    expect(batch.deckIds).toEqual([33, 11]);
    expect(batch.deactivateOthers).toBe(true);
    expect(batch.addToTop).toBe(true);
  });

  it('includes already-studied decks so the server can keep and position them', () => {
    expect(buildPlanStudyBatch(steps, 8, false, false).deckIds).toEqual([11, 22, 33]);
  });
});

describe('planDeckIds', () => {
  it('drops a deck repeated across steps but keeps studied ones', () => {
    expect(planDeckIds([{ deckId: 11 }, { deckId: 11 }, { deckId: 22 }])).toEqual([11, 22]);
  });
});

describe('planStepsToAdd', () => {
  it('leaves out a step whose deck is already studied', () => {
    const studied = [{ deckType: StudyDeckType.MediaDeck, deckId: 22 }];
    expect(planStepsToAdd(steps, studied)).toEqual([11, 33]);
  });

  it('ignores a word list deck that happens to carry the same id', () => {
    const studied = [{ deckType: StudyDeckType.StaticWordList, deckId: 22 }];
    expect(planStepsToAdd(steps, studied)).toEqual([11, 22, 33]);
  });

  it('drops a deck repeated across steps', () => {
    expect(planStepsToAdd([{ deckId: 11 }, { deckId: 11 }], [])).toEqual([11]);
  });
});

describe('isDeckStudied', () => {
  it('matches only media decks', () => {
    expect(isDeckStudied(5, [{ deckType: StudyDeckType.MediaDeck, deckId: 5 }])).toBe(true);
    expect(isDeckStudied(5, [{ deckType: StudyDeckType.GlobalDynamic, deckId: 5 }])).toBe(false);
    expect(isDeckStudied(5, [])).toBe(false);
  });
});
