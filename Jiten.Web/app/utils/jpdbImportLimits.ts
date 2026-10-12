export interface JpdbImportDeck {
  name: string;
  vocabularyCount: number;
}

export interface JpdbImportExistingDeck {
  name: string;
  isWordList: boolean;
  totalWords: number;
}

export interface JpdbImportLimits {
  studyDecks: number;
  studyDeckWords: number;
  importWords: number;
}

export function jpdbListName(name: string) {
  return name.trim().slice(0, 200);
}

/** Mirrors the server's all-or-nothing checks in ImportJpdbDecks; JPDB's vocabulary count stands in for matched words. */
export function jpdbImportLimitReason(selected: JpdbImportDeck[], existing: JpdbImportExistingDeck[], limits: JpdbImportLimits): string {
  const oversized = selected.filter((d) => d.vocabularyCount > limits.importWords);
  if (oversized.length === 1)
    return `"${oversized[0]!.name}" has ${oversized[0]!.vocabularyCount.toLocaleString()} words, over the ${limits.importWords.toLocaleString()}-word limit per deck.`;
  if (oversized.length > 1) return `${oversized.length} selected decks are over the ${limits.importWords.toLocaleString()}-word limit per deck.`;

  const incoming = new Map<string, number>();
  for (const d of selected) {
    const name = jpdbListName(d.name);
    if (!incoming.has(name)) incoming.set(name, d.vocabularyCount);
  }

  const existingListNames = new Set(existing.filter((d) => d.isWordList).map((d) => d.name.trim()));
  const newLists = [...incoming.keys()].filter((name) => !existingListNames.has(name)).length;
  const room = limits.studyDecks - existing.length;
  if (newLists > room) {
    if (room <= 0) return `You're at the ${limits.studyDecks}-deck limit, so only decks that replace one of your lists can be imported.`;
    return `${newLists} new lists selected, but only ${room} more ${room === 1 ? 'fits' : 'fit'} under the ${limits.studyDecks}-deck limit.`;
  }

  const retainedWords = existing.filter((d) => d.isWordList && !incoming.has(d.name.trim())).reduce((sum, d) => sum + d.totalWords, 0);
  const incomingWords = [...incoming.values()].reduce((sum, n) => sum + n, 0);
  const total = retainedWords + incomingWords;
  if (total > limits.studyDeckWords)
    return `Your word lists would reach about ${total.toLocaleString()} words, over the ${limits.studyDeckWords.toLocaleString()}-word limit.`;

  return '';
}
