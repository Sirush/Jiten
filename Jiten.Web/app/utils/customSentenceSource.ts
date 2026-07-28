// Matches the varchar(150) column and the API validation on UserExampleSentence.Source.
export const CUSTOM_SENTENCE_SOURCE_MAX_LENGTH = 150;

/**
 * Sources built from deck titles have no input-level maxlength to stop them, so long
 * parent/child title pairs would be rejected by the API as a generic 400.
 */
export function clampSentenceSource(source: string): string {
  if (source.length <= CUSTOM_SENTENCE_SOURCE_MAX_LENGTH) return source;
  return source.slice(0, CUSTOM_SENTENCE_SOURCE_MAX_LENGTH - 1) + '…';
}
