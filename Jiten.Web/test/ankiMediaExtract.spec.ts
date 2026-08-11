import { describe, expect, it } from 'vitest';
import { extractAudioRef, extractImageRef } from '../app/utils/ankiMediaExtract';

describe('extractImageRef', () => {
  it('reads a plain img source', () => {
    expect(extractImageRef('<img src="cat.jpg">')?.filename).toBe('cat.jpg');
  });

  it('reads single-quoted and unquoted sources', () => {
    expect(extractImageRef("<img src='cat.jpg'>")?.filename).toBe('cat.jpg');
    expect(extractImageRef('<img src=cat.jpg>')?.filename).toBe('cat.jpg');
  });

  it('decodes entities in the filename', () => {
    expect(extractImageRef('<img src="a&amp;b.jpg">')?.filename).toBe('a&b.jpg');
  });

  it('keeps filenames with spaces and unicode verbatim', () => {
    expect(extractImageRef('<img src="paste 猫 1.jpg">')?.filename).toBe('paste 猫 1.jpg');
  });

  it('skips remote and inline sources', () => {
    expect(extractImageRef('<img src="https://example.com/cat.jpg">')).toBeNull();
    expect(extractImageRef('<img src="//example.com/cat.jpg">')).toBeNull();
    expect(extractImageRef('<img src="data:image/png;base64,AAAA">')).toBeNull();
  });

  it('skips svg sources', () => {
    expect(extractImageRef('<img src="diagram.svg">')).toBeNull();
  });

  it('takes the first of several and counts the rest', () => {
    const result = extractImageRef('<img src="a.jpg"><img src="b.jpg"><img src="c.jpg">')!;
    expect(result.filename).toBe('a.jpg');
    expect(result.extraRefs).toBe(2);
  });

  it('falls through a remote image to the first local one', () => {
    expect(extractImageRef('<img src="https://x/a.jpg"><img src="b.jpg">')?.filename).toBe('b.jpg');
  });

  it('returns null when there is no image', () => {
    expect(extractImageRef('これは猫です')).toBeNull();
  });
});

describe('extractAudioRef', () => {
  it('reads a sound tag', () => {
    expect(extractAudioRef('[sound:cat.mp3]')?.filename).toBe('cat.mp3');
  });

  it('reads a sound tag embedded in a sentence field', () => {
    expect(extractAudioRef('これは猫です[sound:neko_01.ogg]')?.filename).toBe('neko_01.ogg');
  });

  it('skips generated tts tags', () => {
    expect(extractAudioRef('[sound:anki:tts lang=ja_JP voices=Kyoko:猫]')).toBeNull();
  });

  it('skips video containers whose audio track would otherwise be accepted', () => {
    expect(extractAudioRef('[sound:clip.mp4]')).toBeNull();
    expect(extractAudioRef('[sound:clip.mkv]')).toBeNull();
  });

  it('takes the first of several and counts the rest', () => {
    const result = extractAudioRef('[sound:a.mp3][sound:b.mp3]')!;
    expect(result.filename).toBe('a.mp3');
    expect(result.extraRefs).toBe(1);
  });

  it('returns null when there is no audio', () => {
    expect(extractAudioRef('これは猫です')).toBeNull();
  });
});
