interface MatchFlag {
  isExactMatch: boolean;
}

export function hasExactDuplicate(decks: MatchFlag[], requests: MatchFlag[]): boolean {
  return decks.some((d) => d.isExactMatch) || requests.some((r) => r.isExactMatch);
}

export function isDuplicateGateSatisfied(hasExactMatch: boolean, acknowledged: boolean): boolean {
  return !hasExactMatch || acknowledged;
}
