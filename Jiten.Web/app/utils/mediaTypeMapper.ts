import { MediaType, MediaTypeGroup } from '~/types';

export function getMediaTypeText(mediaType: MediaType): string {
  switch (mediaType) {
    case MediaType.Anime:
      return 'Anime';
    case MediaType.Drama:
      return 'Drama';
    case MediaType.Movie:
      return 'Movie';
    case MediaType.Novel:
      return 'Novel';
    case MediaType.NonFiction:
      return 'Non-Fiction';
    case MediaType.VideoGame:
      return 'Video Game';
    case MediaType.VisualNovel:
      return 'Visual Novel';
    case MediaType.WebNovel:
      return 'Web Novel';
    case MediaType.Manga:
      return 'Manga';
    case MediaType.Audio:
      return 'Audio';
    default:
      return 'Unknown';
  }
}

export function getChildrenCountText(mediaType: MediaType): string {
  switch (mediaType) {
    case MediaType.Anime:
      return 'Episodes';
    case MediaType.Drama:
      return 'Episodes';
    case MediaType.Movie:
      return 'Movies';
    case MediaType.Manga:
      return 'Volumes';
    case MediaType.Novel:
      return 'Volumes';
    case MediaType.NonFiction:
      return 'Volumes';
    case MediaType.VideoGame:
      return 'Entries';
    case MediaType.VisualNovel:
      return 'Routes';
    case MediaType.WebNovel:
      return 'Parts';
    case MediaType.Audio:
      return 'Entries';
    default:
      return 'Unknown';
  }
}

export function getMediaTypeGroupText(group: MediaTypeGroup): string {
  switch (group) {
    case MediaTypeGroup.Prose:
      return 'Prose';
    case MediaTypeGroup.VisualText:
      return 'Visual Text';
    case MediaTypeGroup.AudioVisual:
      return 'Audio Visual';
    case MediaTypeGroup.NonFiction:
      return 'Non-Fiction';
    default:
      return 'Unknown';
  }
}
