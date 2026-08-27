import type { BatchAddStudyDecksRequest } from '~/types';
import { DeckDownloadType, StudyDeckType } from '~/types';

export interface PlanStudyStep {
  deckId: number;
}

export interface StudiedDeckRef {
  deckType: StudyDeckType;
  deckId?: number;
}

export function isDeckStudied(deckId: number, studyDecks: StudiedDeckRef[]): boolean {
  return studyDecks.some((d) => d.deckType === StudyDeckType.MediaDeck && d.deckId === deckId);
}

export function planStepsToAdd(steps: PlanStudyStep[], studyDecks: StudiedDeckRef[]): number[] {
  const seen = new Set<number>();
  const result: number[] = [];
  for (const step of steps) {
    if (seen.has(step.deckId) || isDeckStudied(step.deckId, studyDecks)) continue;
    seen.add(step.deckId);
    result.push(step.deckId);
  }
  return result;
}

// Already-studied decks are sent too: the server skips them but needs the full plan
// order to keep them active under deactivateOthers and to position them for addToTop.
export function planDeckIds(steps: PlanStudyStep[]): number[] {
  const seen = new Set<number>();
  const result: number[] = [];
  for (const step of steps) {
    if (seen.has(step.deckId)) continue;
    seen.add(step.deckId);
    result.push(step.deckId);
  }
  return result;
}

export function buildPlanStudyBatch(steps: PlanStudyStep[], minOccurrences: number, deactivateOthers: boolean, addToTop: boolean): BatchAddStudyDecksRequest {
  return {
    deckIds: planDeckIds(steps),
    downloadType: DeckDownloadType.OccurrenceCount,
    minOccurrences,
    deactivateOthers,
    addToTop,
  };
}
