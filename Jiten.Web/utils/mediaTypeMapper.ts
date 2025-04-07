import { MediaType } from '~/types';

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
      return 'Chapters';
    default:
      return 'Unknown';
  }
}
