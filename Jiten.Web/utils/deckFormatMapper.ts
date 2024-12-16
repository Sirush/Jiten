import { DeckFormat } from '~/types';

export function getDeckFormatText(deckFormat: DeckFormat): string {
  switch (deckFormat) {
    case DeckFormat.Anki:
      return 'Anki';
    case DeckFormat.Csv:
      return 'CSV';
    default:
      return 'Unknown';
  }
}
