import type { StudySettingsDto } from '~/types';

export type CardAudioSlot = 'headword' | 'clip' | 'sentence';

export interface CardAudioContext {
  onFront: boolean;
  /** Manual replay: the autoplay toggles and the front/back position are ignored. */
  forced: boolean;
  hasClip: boolean;
  hasSentence: boolean;
  isNewCard: boolean;
  frontHasSentence: boolean;
  sentenceBlurred: boolean;
  ttsMuted: boolean;
}

export interface CardAudioPlan {
  slots: CardAudioSlot[];
  /** Slots the clip stands in for; play them after all when the clip fails to sound. */
  fallback: CardAudioSlot[];
}

export type SentenceAudioSource = 'clip' | 'tts' | 'none';

export interface SentenceAudioContext {
  onFront: boolean;
  hasClip: boolean;
  hasSentence: boolean;
  ttsMuted: boolean;
}

function clipPlaysOnSide(settings: StudySettingsDto, context: { onFront: boolean; forced: boolean; hasClip: boolean }): boolean {
  const position = settings.autoPlayCustomAudioPosition;
  const thisSide = context.forced || position === 'Both' || position === (context.onFront ? 'Front' : 'Back');
  return context.hasClip && (context.forced || settings.autoPlayCustomAudio) && thisSide;
}

/**
 * Which source owns the sentence slot on this side once the sentence is visible. The clip only owns it on the
 * side it actually plays on, so revealing a blurred sentence elsewhere still gets its text-to-speech.
 */
export function resolveSentenceAudioSource(settings: StudySettingsDto, context: SentenceAudioContext): SentenceAudioSource {
  if (!context.hasSentence) return 'none';
  const clip = clipPlaysOnSide(settings, { onFront: context.onFront, forced: false, hasClip: context.hasClip });
  if (clip && settings.customAudioReplacesSentence) return 'clip';
  const wanted = context.onFront ? settings.autoPlaySentenceOnFront : settings.autoPlaySentence;
  return wanted && !context.ttsMuted ? 'tts' : 'none';
}

/** Orders the card's audio by slot, so a clip standing in for the sentence plays where the sentence would have. */
export function buildCardAudioPlan(settings: StudySettingsDto, context: CardAudioContext): CardAudioPlan {
  const { onFront, forced } = context;
  const ttsAudible = !context.ttsMuted;

  let headword = ttsAudible && (forced || (onFront ? settings.autoPlayWordOnFront : settings.autoPlayWord));
  if (!forced && onFront && headword && settings.autoPlayWordOnFrontNewOnly && !context.isNewCard) headword = false;

  const clip = clipPlaysOnSide(settings, { onFront, forced, hasClip: context.hasClip });

  const replacesHeadword = clip && settings.customAudioReplacesHeadword;
  const replacesSentence = clip && settings.customAudioReplacesSentence;

  const sentence =
    ttsAudible &&
    context.hasSentence &&
    (forced
      ? onFront
        ? context.frontHasSentence
        : !context.sentenceBlurred
      : onFront
        ? context.frontHasSentence && settings.autoPlayWordOnFront && settings.autoPlaySentenceOnFront
        : settings.autoPlaySentence && !context.sentenceBlurred);

  const slots: CardAudioSlot[] = [];
  if (replacesHeadword) slots.push('clip');
  else if (headword) slots.push('headword');
  if (clip && !replacesHeadword) slots.push('clip');
  if (sentence && !replacesSentence) slots.push('sentence');

  const fallback: CardAudioSlot[] = [];
  if (replacesHeadword && headword) fallback.push('headword');
  if (replacesSentence && sentence) fallback.push('sentence');

  return { slots, fallback };
}
