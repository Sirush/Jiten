import type { ProfileHeader, ProfileOverview } from '~/types';
import { formatMonthYear } from '~/utils/dateFormat';
import { formatCompact } from '~/utils/siteStats';

export function profileSnapshotTiles(snapshot: ProfileOverview['snapshot'] | null | undefined): { label: string; value: string }[] {
  if (!snapshot) return [];
  return [
    { label: 'Words known', value: snapshot.wordsKnown },
    { label: 'Kanji known', value: snapshot.kanjiKnown },
    { label: 'Characters', value: snapshot.characters },
    { label: 'Titles finished', value: snapshot.titlesFinished },
  ]
    .filter((x): x is { label: string; value: number } => !!x.value)
    .map((x) => ({ label: x.label, value: formatCompact(x.value) }));
}

export function profileOgImageProps(header: ProfileHeader, overview?: ProfileOverview | null) {
  return {
    name: header.displayName ?? '',
    avatarKanji: header.avatarKanji,
    avatarColour: header.avatarColour,
    avatarStrokes: header.avatarStyle === 'stroke' ? header.avatarStrokes : null,
    headerColour: header.headerColour,
    memberSince: header.memberSince ? formatMonthYear(header.memberSince, { utc: true }) : null,
    stats: profileSnapshotTiles(overview?.snapshot),
    coverNames: (overview?.pinned ?? []).map((item) => item.deck.coverName).filter((name): name is string => !!name),
  };
}
