import { CardBatchKind, CardSource } from '~/types/enums';
import type { CardBatch, CardBatchDeck } from '~/types/types';
import { getDeckStatusText } from '~/utils/deckStatusMapper';

const CARD_SOURCE_DISPLAY: Record<CardSource, { label: string; icon: string }> = {
  [CardSource.Unknown]: { label: 'Other', icon: 'pi pi-question-circle' },
  [CardSource.Review]: { label: 'Reviews', icon: 'pi pi-replay' },
  [CardSource.ManualSingle]: { label: 'Added by hand', icon: 'pi pi-pencil' },
  [CardSource.DeckLearn]: { label: 'Deck', icon: 'pi pi-book' },
  [CardSource.FrequencyImport]: { label: 'Frequency range', icon: 'pi pi-chart-bar' },
  [CardSource.AnkiImport]: { label: 'Anki', icon: 'pi pi-sync' },
  [CardSource.JpdbImport]: { label: 'JPDB', icon: 'pi pi-cloud-download' },
  [CardSource.WaniKaniImport]: { label: 'WaniKani', icon: 'pi pi-cloud-download' },
  [CardSource.WordListImport]: { label: 'Word list', icon: 'pi pi-list' },
  [CardSource.BackupRestore]: { label: 'Backup', icon: 'pi pi-database' },
  [CardSource.CompositionInference]: { label: 'Composition inference', icon: 'pi pi-sitemap' },
  [CardSource.PlacementTest]: { label: 'Word check', icon: 'pi pi-check-square' },
  [CardSource.ImportExtrapolation]: { label: 'Filled in from an import', icon: 'pi pi-arrows-h' },
  [CardSource.KanaRedundancy]: { label: 'Kana forms', icon: 'pi pi-clone' },
  [CardSource.MassAction]: { label: 'Mass action', icon: 'pi pi-list-check' },
  [CardSource.WordSet]: { label: 'Word set', icon: 'pi pi-th-large' },
  [CardSource.ReadingCalibration]: { label: 'Reading check', icon: 'pi pi-sliders-h' },
};

export function cardSourceDisplay(source: CardSource): { label: string; icon: string } {
  return CARD_SOURCE_DISPLAY[source] ?? CARD_SOURCE_DISPLAY[CardSource.Unknown];
}

/** The line under a batch's source label, or null when the label says it all. */
export function batchDetail(batch: Pick<CardBatch, 'details' | 'deck'>, localiseTitle: (deck: CardBatchDeck) => string): string | null {
  const { kind, name, minRank, maxRank, units, listStatus } = batch.details;
  switch (kind) {
    case CardBatchKind.JpdbKnownWords:
      return 'Known words';
    case CardBatchKind.JpdbReviewHistory:
      return 'Review history';
    case CardBatchKind.WordIds:
      return 'Word IDs';
    case CardBatchKind.AnkiFile:
    case CardBatchKind.StudyDeck:
      return name ?? null;
    case CardBatchKind.AnkiConnect:
      return name ? `AnkiConnect: ${name}` : 'AnkiConnect';
    case CardBatchKind.CompoundsOfKnown:
      return 'Compounds of words you know';
    case CardBatchKind.PartsOfKnown:
      return 'Parts of words you know';
    case CardBatchKind.FrequencyRange:
      return minRank != null && maxRank != null ? `Ranks ${minRank.toLocaleString()} to ${maxRank.toLocaleString()}` : null;
    case CardBatchKind.MediaDeck:
      return batch.deck ? localiseTitle(batch.deck) : null;
    case CardBatchKind.MediaList:
      if (!name) return null;
      return listStatus != null ? `${name}'s media list (${getDeckStatusText(listStatus).toLowerCase()})` : `${name}'s media list`;
    case CardBatchKind.WordCheck:
      return 'The common words you know';
    case CardBatchKind.ReadingCheck:
      if (units == null) return null;
      return units === 1 ? '1 volume or work' : `${units.toLocaleString()} volumes and works`;
    default:
      return null;
  }
}
