import { DeckFormat } from '~/types';

export function getDeckFormatText(deckFormat: DeckFormat): string {
  switch (deckFormat) {
    case DeckFormat.Anki:
      return 'Anki';
    case DeckFormat.Csv:
      return 'CSV';
    case DeckFormat.Txt:
      return 'TXT (vocab only)';
    case DeckFormat.TxtRepeated:
      return 'TXT (repeated vocab)';
    default:
      return 'Unknown';
  }
}
