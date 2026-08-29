import { describe, expect, it } from 'vitest';
import { buildCardAudioPlan, type CardAudioContext } from '../app/utils/cardAudioPlan';
import type { StudySettingsDto } from '../app/types/types';

// The planner only reads the autoplay fields below; the rest of StudySettingsDto is irrelevant here.
function settings(overrides: Partial<StudySettingsDto> = {}): StudySettingsDto {
  return {
    autoPlayWord: true,
    autoPlaySentence: true,
    autoPlayWordOnFront: false,
    autoPlayWordOnFrontNewOnly: false,
    autoPlaySentenceOnFront: false,
    autoPlayCustomAudio: true,
    autoPlayCustomAudioPosition: 'Both',
    customAudioReplacesHeadword: true,
    customAudioReplacesSentence: true,
    ...overrides,
  } as StudySettingsDto;
}

function context(overrides: Partial<CardAudioContext> = {}): CardAudioContext {
  return {
    onFront: false,
    forced: false,
    hasClip: true,
    hasSentence: true,
    isNewCard: false,
    frontHasSentence: false,
    sentenceBlurred: false,
    ttsMuted: false,
    ...overrides,
  };
}

describe('buildCardAudioPlan', () => {
  it('plays the clip alone when it replaces both the headword and the sentence', () => {
    const plan = buildCardAudioPlan(settings(), context());
    expect(plan.slots).toEqual(['clip']);
    expect(plan.fallback).toEqual(['headword', 'sentence']);
  });

  it('plays the clip then the sentence when it replaces the headword only', () => {
    const plan = buildCardAudioPlan(settings({ customAudioReplacesSentence: false }), context());
    expect(plan.slots).toEqual(['clip', 'sentence']);
    expect(plan.fallback).toEqual(['headword']);
  });

  it('plays the headword then the clip when it replaces the sentence only', () => {
    const plan = buildCardAudioPlan(settings({ customAudioReplacesHeadword: false }), context());
    expect(plan.slots).toEqual(['headword', 'clip']);
    expect(plan.fallback).toEqual(['sentence']);
  });

  it('plays headword, clip and sentence when the clip replaces neither', () => {
    const plan = buildCardAudioPlan(settings({ customAudioReplacesHeadword: false, customAudioReplacesSentence: false }), context());
    expect(plan.slots).toEqual(['headword', 'clip', 'sentence']);
    expect(plan.fallback).toEqual([]);
  });

  it('plays the clip alone when it replaces the sentence and headword autoplay is off', () => {
    const plan = buildCardAudioPlan(settings({ customAudioReplacesHeadword: false, autoPlayWord: false }), context());
    expect(plan.slots).toEqual(['clip']);
    expect(plan.fallback).toEqual(['sentence']);
  });

  it('falls back to nothing when the replaced slots were not going to play anyway', () => {
    const plan = buildCardAudioPlan(settings({ autoPlayWord: false, autoPlaySentence: false }), context());
    expect(plan.slots).toEqual(['clip']);
    expect(plan.fallback).toEqual([]);
  });

  it('drops the clip slot when the card has no clip', () => {
    const plan = buildCardAudioPlan(settings(), context({ hasClip: false }));
    expect(plan.slots).toEqual(['headword', 'sentence']);
    expect(plan.fallback).toEqual([]);
  });

  it('drops the sentence slot when the card has no example sentence', () => {
    const plan = buildCardAudioPlan(settings({ customAudioReplacesHeadword: false, customAudioReplacesSentence: false }), context({ hasSentence: false }));
    expect(plan.slots).toEqual(['headword', 'clip']);
  });

  it('returns an empty plan when nothing is enabled', () => {
    const plan = buildCardAudioPlan(settings({ autoPlayWord: false, autoPlaySentence: false, autoPlayCustomAudio: false }), context());
    expect(plan.slots).toEqual([]);
  });

  describe('position', () => {
    it('skips the clip on the back when it is set to the front only', () => {
      const plan = buildCardAudioPlan(settings({ autoPlayCustomAudioPosition: 'Front' }), context());
      expect(plan.slots).toEqual(['headword', 'sentence']);
    });

    it('skips the clip on the front when it is set to the back only', () => {
      const plan = buildCardAudioPlan(
        settings({ autoPlayCustomAudioPosition: 'Back', autoPlayWordOnFront: true, autoPlaySentenceOnFront: true }),
        context({ onFront: true, frontHasSentence: true })
      );
      expect(plan.slots).toEqual(['headword', 'sentence']);
    });

    it('plays the clip on the front when it is set to the front only', () => {
      const plan = buildCardAudioPlan(
        settings({ autoPlayCustomAudioPosition: 'Front', customAudioReplacesHeadword: false, autoPlayWordOnFront: true }),
        context({ onFront: true })
      );
      expect(plan.slots).toEqual(['headword', 'clip']);
    });
  });

  describe('front side', () => {
    it('plays nothing on the front when front autoplay is off', () => {
      const plan = buildCardAudioPlan(settings({ autoPlayCustomAudio: false }), context({ onFront: true }));
      expect(plan.slots).toEqual([]);
    });

    it('skips the headword on the front for a review card when new-only is set', () => {
      const plan = buildCardAudioPlan(
        settings({ customAudioReplacesHeadword: false, autoPlayWordOnFront: true, autoPlayWordOnFrontNewOnly: true }),
        context({ onFront: true, isNewCard: false })
      );
      expect(plan.slots).toEqual(['clip']);
      expect(plan.fallback).toEqual([]);
    });

    it('keeps the headword on the front for a new card when new-only is set', () => {
      const plan = buildCardAudioPlan(
        settings({ customAudioReplacesHeadword: false, autoPlayWordOnFront: true, autoPlayWordOnFrontNewOnly: true }),
        context({ onFront: true, isNewCard: true })
      );
      expect(plan.slots).toEqual(['headword', 'clip']);
    });

    it('needs the sentence on the front layout to play it there', () => {
      const plan = buildCardAudioPlan(
        settings({
          customAudioReplacesHeadword: false,
          customAudioReplacesSentence: false,
          autoPlayWordOnFront: true,
          autoPlaySentenceOnFront: true,
        }),
        context({ onFront: true, frontHasSentence: false })
      );
      expect(plan.slots).toEqual(['headword', 'clip']);
    });
  });

  describe('blurred sentence', () => {
    it('skips the sentence on the back while it is blurred', () => {
      const plan = buildCardAudioPlan(settings({ customAudioReplacesHeadword: false, customAudioReplacesSentence: false }), context({ sentenceBlurred: true }));
      expect(plan.slots).toEqual(['headword', 'clip']);
    });
  });

  describe('muted TTS', () => {
    it('keeps only the clip when the clip replaces neither slot', () => {
      const plan = buildCardAudioPlan(settings({ customAudioReplacesHeadword: false, customAudioReplacesSentence: false }), context({ ttsMuted: true }));
      expect(plan.slots).toEqual(['clip']);
      expect(plan.fallback).toEqual([]);
    });

    it('does not fall back to the headword the clip replaces', () => {
      const plan = buildCardAudioPlan(settings({ customAudioReplacesSentence: false }), context({ ttsMuted: true }));
      expect(plan.slots).toEqual(['clip']);
      expect(plan.fallback).toEqual([]);
    });

    it('plans nothing when the card has no clip', () => {
      const plan = buildCardAudioPlan(settings(), context({ ttsMuted: true, hasClip: false }));
      expect(plan.slots).toEqual([]);
      expect(plan.fallback).toEqual([]);
    });

    it('still drops the TTS slots on a manual replay', () => {
      const plan = buildCardAudioPlan(
        settings({ customAudioReplacesHeadword: false, customAudioReplacesSentence: false }),
        context({ ttsMuted: true, forced: true })
      );
      expect(plan.slots).toEqual(['clip']);
      expect(plan.fallback).toEqual([]);
    });

    it('leaves the plan untouched when the volume is up', () => {
      const plan = buildCardAudioPlan(settings({ customAudioReplacesHeadword: false, customAudioReplacesSentence: false }), context({ ttsMuted: false }));
      expect(plan.slots).toEqual(['headword', 'clip', 'sentence']);
      expect(plan.fallback).toEqual([]);
    });
  });

  describe('manual replay', () => {
    it('ignores the autoplay toggles and the position but keeps the replace rules', () => {
      const plan = buildCardAudioPlan(
        settings({
          autoPlayWord: false,
          autoPlaySentence: false,
          autoPlayCustomAudio: false,
          autoPlayCustomAudioPosition: 'Front',
          customAudioReplacesHeadword: false,
        }),
        context({ forced: true })
      );
      expect(plan.slots).toEqual(['headword', 'clip']);
      expect(plan.fallback).toEqual(['sentence']);
    });

    it('still respects a blurred sentence on the back', () => {
      const plan = buildCardAudioPlan(
        settings({ customAudioReplacesHeadword: false, customAudioReplacesSentence: false }),
        context({ forced: true, sentenceBlurred: true })
      );
      expect(plan.slots).toEqual(['headword', 'clip']);
    });
  });
});
