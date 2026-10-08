import { DeckStatus, MediaListEntryState, MediaType } from '~/types/enums';
import type { Deck, MediaListEntry, MediaListEntrySummary } from '~/types/types';
import { getChildrenCountText } from '~/utils/mediaTypeMapper';

/** Highest character count the server accepts on an entry. */
export const MAX_CHARACTERS_READ = 100_000_000;

const WATCHED_TYPES = [MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio, MediaType.YouTube];

export interface MediaWords {
  noun: string;
  past: string;
  start: string;
  again: string;
  historyTitle: string;
}

export function mediaWords(mediaType: MediaType): MediaWords {
  if (mediaType === MediaType.Audio)
    return { noun: 'listen', past: 'Listened to', start: 'Start listening', again: 'Listen again', historyTitle: 'Listening history' };
  if (WATCHED_TYPES.includes(mediaType))
    return { noun: 'viewing', past: 'Watched', start: 'Start watching', again: 'Watch again', historyTitle: 'Watch history' };
  if (mediaType === MediaType.VideoGame)
    return { noun: 'playthrough', past: 'Played', start: 'Start playing', again: 'Play again', historyTitle: 'Play history' };
  return { noun: 'read', past: 'Read', start: 'Start reading', again: 'Read again', historyTitle: 'Reading history' };
}

/** "reading", "watching", "listening", "playing": what someone is still doing with a title in progress. */
export function ongoingVerb(mediaType: MediaType): string {
  if (mediaType === MediaType.Audio) return 'listening';
  if (WATCHED_TYPES.includes(mediaType)) return 'watching';
  if (mediaType === MediaType.VideoGame) return 'playing';
  return 'reading';
}

/** Watched and listened media count progress against their subtitles or transcript, so it reads as a share rather than characters read. */
function isHeardOrWatched(mediaType: MediaType): boolean {
  return WATCHED_TYPES.includes(mediaType);
}

/** Label of the progress field: "Characters read", or "Watched so far" where characters are not what the viewer thinks in. */
export function progressFieldLabel(mediaType: MediaType): string {
  if (!isHeardOrWatched(mediaType)) return 'Characters read';
  return mediaType === MediaType.Audio ? 'Listened so far' : 'Watched so far';
}

/** What moving to Ongoing or Paused asks first; away from Completed, a completion always gets a new pass, so only a stopped pass is worth asking about. */
export function restartQuestion(status: DeckStatus | undefined, entry: MediaListEntrySummary | null | undefined): 'reread' | 'resume' | null {
  if (status === DeckStatus.Completed) return 'reread';
  if ((status === DeckStatus.Dropped || status === DeckStatus.Planning) && entry?.state === MediaListEntryState.Dropped) return 'resume';
  return null;
}

/** What dropping asks first: whether a Completed title was really finished, or when a pass under way stopped; anything else records no pass. */
export function dropQuestion(status: DeckStatus | undefined, entry: MediaListEntrySummary | null | undefined): 'unfinish' | 'stopped' | null {
  if (status === DeckStatus.Completed && entry?.state === MediaListEntryState.Completed) return 'unfinish';
  return entry?.state === MediaListEntryState.InProgress ? 'stopped' : null;
}

/** The day in the viewer's calendar; the server stores dates as the user sees them. */
export function dateToIso(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

export function isoToday(): string {
  return dateToIso(new Date());
}

export function isoToDate(iso: string): Date {
  const [year, month, day] = iso.slice(0, 10).split('-').map(Number);
  return new Date(year!, month! - 1, day!);
}

/** Bounds of every date picker on a media list entry; the server accepts nothing earlier. */
export const ENTRY_MIN_DATE = new Date(1900, 0, 1);

export function entryMaxDate(): Date {
  return new Date();
}

/** Start of the pass a Completed or Dropped change would close, which its finish date may not precede; a planned title's set-aside pass stays closed. */
export function closingPassStart(
  entry: MediaListEntrySummary | null | undefined,
  status: DeckStatus | undefined,
  { undoCompletion = false } = {}
): string | null {
  if (!entry) return null;
  if (entry.state === MediaListEntryState.InProgress) return entry.startedOn;
  if (entry.state === MediaListEntryState.Dropped && status !== DeckStatus.Planning) return entry.startedOn;
  return undoCompletion && entry.state === MediaListEntryState.Completed ? entry.startedOn : null;
}

/** Plural for the parts of a series on a media list; audio and game parts are not "entries", which names a pass in the history. */
export function unitWord(mediaType: MediaType): string {
  if (mediaType === MediaType.Audio || mediaType === MediaType.VideoGame) return 'Parts';
  return getChildrenCountText(mediaType);
}

/** "4 of 12 volumes" */
export function unitsFact(entry: Pick<MediaListEntrySummary, 'unitCount' | 'completedUnits'>, mediaType: MediaType): string | null {
  return entry.unitCount ? `${entry.completedUnits ?? 0} of ${entry.unitCount} ${unitWord(mediaType).toLowerCase()}` : null;
}

const UNTRACKED_UNIT_TYPES = [MediaType.VisualNovel, MediaType.YouTube, MediaType.VideoGame];

/** Series whose volumes or episodes can be counted off from the progress step; the server applies the same media types. */
export function canTrackUnits(deck: Pick<Deck, 'parentDeckId' | 'childrenDeckCount' | 'mediaType'>): boolean {
  return !deck.parentDeckId && deck.childrenDeckCount > 0 && !UNTRACKED_UNIT_TYPES.includes(deck.mediaType);
}

/** "Volumes read", "Episodes watched": the label for a series' completed units. */
export function unitProgressLabel(mediaType: MediaType): string {
  return `${unitWord(mediaType)} ${mediaWords(mediaType).past.toLowerCase()}`;
}

/** Characters an unfinished entry stands for: a series counts its volumes until its own count goes past them, as the server does. */
export function entryCharacters(entry: Pick<MediaListEntrySummary, 'charactersRead' | 'volumeCharacters'> | null | undefined): number | null {
  const own = entry?.charactersRead ?? null;
  const volumes = entry?.volumeCharacters ?? null;
  if (own == null) return volumes;
  return volumes == null ? own : Math.max(own, volumes);
}

/** Whole percent read, rounded down so 100% only shows once the whole deck is read; anything logged shows at least 1%. Can go past 100. */
export function rawPercentOf(characters: number, deckCharacters: number): number {
  if (characters <= 0) return 0;
  return Math.max(1, Math.floor((characters * 100) / deckCharacters));
}

export function percentOf(characters: number, deckCharacters: number): number {
  return Math.min(100, rawPercentOf(characters, deckCharacters));
}

/** Short progress for the status pill and progress buttons: "34%" for a title, "4/12" for a series. */
export function entryProgressLabel(entry: MediaListEntrySummary | null | undefined, deckCharacters: number): string | null {
  if (entry?.state !== MediaListEntryState.InProgress) return null;
  if (entry.unitCount) return `${entry.completedUnits ?? 0}/${entry.unitCount}`;
  const percent = entryProgressPercent(entry, deckCharacters);
  return percent == null ? null : `${percent}%`;
}

/** Share of the deck read so far on an unfinished entry, or null when nothing has been logged. */
export function entryProgressPercent(entry: MediaListEntrySummary | null | undefined, deckCharacters: number): number | null {
  const characters = entryCharacters(entry);
  if (entry?.state !== MediaListEntryState.InProgress || !characters || deckCharacters <= 0) return null;
  return percentOf(characters, deckCharacters);
}

export function formatReadDate(iso: string): string {
  return isoToDate(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

/** The release date as a pick, when it is known and not in the future. */
export function releaseDateChoice(releaseDate: string | Date | null | undefined, today: string = isoToday()): string | null {
  if (!releaseDate) return null;
  const iso = typeof releaseDate === 'string' ? releaseDate.slice(0, 10) : dateToIso(releaseDate);
  return iso >= '1900-01-01' && iso <= today ? iso : null;
}

const compact = new Intl.NumberFormat('en', { notation: 'compact', maximumFractionDigits: 1 });

export function formatCharacters(count: number): string {
  return count < 10_000 ? count.toLocaleString() : compact.format(count);
}

/** Short facts about a media list entry for the card and the list, most important first. */
export function listEntryFacts(entry: MediaListEntrySummary, mediaType: MediaType, deckCharacters: number, { showUnknownDate = true } = {}): string[] {
  const facts: string[] = [];
  const words = mediaWords(mediaType);

  if (entry.state === MediaListEntryState.InProgress) {
    if (entry.startedOn) facts.push(`Started ${formatReadDate(entry.startedOn)}`);
  } else if (entry.state === MediaListEntryState.Completed) {
    if (entry.finishedOn) facts.push(`Finished ${formatReadDate(entry.finishedOn)}`);
    else if (showUnknownDate) facts.push('Finish date unknown');
  } else if (entry.state === MediaListEntryState.Dropped && entry.finishedOn) {
    facts.push(`Dropped ${formatReadDate(entry.finishedOn)}`);
  } else if (entry.state == null && entry.finishedOn) {
    facts.push(`Last finished ${formatReadDate(entry.finishedOn)}`);
  }

  if (entry.completedCount >= 2) facts.push(`${words.past} ${entry.completedCount} times`);

  const units = entry.state !== MediaListEntryState.Completed ? unitsFact(entry, mediaType) : null;
  if (units) facts.push(units);

  const characters = entry.state === MediaListEntryState.Completed ? entry.charactersRead : entry.charactersRead != null ? entryCharacters(entry) : null;
  if (characters != null) {
    if (isHeardOrWatched(mediaType) && deckCharacters > 0 && entry.state !== MediaListEntryState.Completed) {
      const verb = mediaType === MediaType.Audio ? 'listened' : 'watched';
      facts.push(`${percentOf(characters, deckCharacters)}% ${verb}`);
    } else {
      facts.push(
        deckCharacters > 0 ? `${formatCharacters(characters)} of ${formatCharacters(deckCharacters)} characters` : `${formatCharacters(characters)} characters`
      );
    }
  }

  return facts;
}

/** Characters per day over a dated, completed entry; null when either date is missing. */
export function entryPace(entry: Pick<MediaListEntry, 'state' | 'startedOn' | 'finishedOn' | 'charactersRead'>, deckCharacters: number): number | null {
  if (entry.state !== MediaListEntryState.Completed || !entry.startedOn || !entry.finishedOn) return null;
  const characters = entry.charactersRead ?? deckCharacters;
  if (characters <= 0) return null;
  const days = Math.round((isoToDate(entry.finishedOn).getTime() - isoToDate(entry.startedOn).getTime()) / 86_400_000) + 1;
  return days > 0 ? Math.round(characters / days) : null;
}
