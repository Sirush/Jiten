import { describe, expect, it } from 'vitest';
import { DEFAULT_TTS_VOLUME, isTtsMuted, resolveTtsVolume } from '../app/utils/ttsVolume';

describe('resolveTtsVolume', () => {
  it('defaults to full volume', () => {
    expect(DEFAULT_TTS_VOLUME).toBe(1);
    expect(resolveTtsVolume(undefined)).toBe(1);
  });

  it('passes an in-range value through', () => {
    expect(resolveTtsVolume(0.3)).toBe(0.3);
    expect(resolveTtsVolume(0)).toBe(0);
    expect(resolveTtsVolume(1)).toBe(1);
  });

  it('clamps at both ends', () => {
    expect(resolveTtsVolume(1.8)).toBe(1);
    expect(resolveTtsVolume(-0.5)).toBe(0);
  });

  it('falls back to the default for a corrupt stored value', () => {
    expect(resolveTtsVolume(null)).toBe(1);
    expect(resolveTtsVolume('0.4')).toBe(1);
    expect(resolveTtsVolume(NaN)).toBe(1);
    expect(resolveTtsVolume(Infinity)).toBe(1);
    expect(resolveTtsVolume({ volume: 0.4 })).toBe(1);
  });
});

describe('isTtsMuted', () => {
  it('is true only at zero', () => {
    expect(isTtsMuted(0)).toBe(true);
    expect(isTtsMuted(-1)).toBe(true);
    expect(isTtsMuted(0.05)).toBe(false);
    expect(isTtsMuted(undefined)).toBe(false);
  });
});
