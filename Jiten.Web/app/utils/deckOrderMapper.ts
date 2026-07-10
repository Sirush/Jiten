import { DeckOrder } from '~/types';

export function getDeckOrderText(deckOrder: DeckOrder): string {
  switch (deckOrder) {
    case DeckOrder.Chronological:
      return 'Chronological';
    case DeckOrder.DeckFrequency:
      return 'Deck Frequency';
    case DeckOrder.GlobalFrequency:
      return 'Global Frequency';
    case DeckOrder.ImportOrder:
      return 'Import Order';
    case DeckOrder.Random:
      return 'Random';
    default:
      return 'Unknown';
  }
}
