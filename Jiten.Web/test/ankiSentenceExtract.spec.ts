import { describe, expect, it } from 'vitest';
import {
  buildSentenceForImport,
  extractSentenceFromField,
  foldKana,
  locateWord,
  SENTENCE_MAX_LENGTH,
  truncateAroundWord,
} from '../app/utils/ankiSentenceExtract';

const marked = (html: string, candidates: string[]) => buildSentenceForImport(html, candidates).text;

describe('extractSentenceFromField', () => {
  it('flattens markup and decodes entities', () => {
    const result = extractSentenceFromField('<div>これは&quot;猫&quot;です<br>ね</div>');
    expect(result.text).toBe('これは"猫"ですね');
  });

  it('records bold runs as marked ranges', () => {
    const result = extractSentenceFromField('これは<b>猫</b>です');
    expect(result.text).toBe('これは猫です');
    expect(result.markedRanges).toEqual([[3, 4]]);
  });

  it('treats a coloured span as a marker', () => {
    const result = extractSentenceFromField('これは<span style="color: rgb(255,0,0);">猫</span>です');
    expect(result.markedRanges).toEqual([[3, 4]]);
  });

  it('ignores a span with unrelated styling', () => {
    const result = extractSentenceFromField('これは<span style="font-size: 20px;">猫</span>です');
    expect(result.markedRanges).toEqual([]);
  });

  it('unwraps cloze deletions and marks the body', () => {
    const result = extractSentenceFromField('これは{{c1::猫}}です');
    expect(result.text).toBe('これは猫です');
    expect(result.markedRanges).toEqual([[3, 4]]);
  });

  it('unwraps a cloze carrying a hint', () => {
    const result = extractSentenceFromField('これは{{c1::猫::動物}}です');
    expect(result.text).toBe('これは猫です');
    expect(result.markedRanges).toEqual([[3, 4]]);
  });

  it('drops sound tags and furigana brackets', () => {
    const result = extractSentenceFromField('[sound:cat.mp3]私[わたし]は猫[ねこ]です');
    expect(result.text).toBe('私は猫です');
  });

  it('drops ruby readings but keeps the base text', () => {
    const result = extractSentenceFromField('<ruby>私<rt>わたし</rt></ruby>は猫です');
    expect(result.text).toBe('私は猫です');
  });

  it('removes literal asterisks so they cannot break the marker parse', () => {
    const result = extractSentenceFromField('これは*猫*です');
    expect(result.text).toBe('これは猫です');
  });

  it('collapses nbsp runs', () => {
    const result = extractSentenceFromField('これは&nbsp;&nbsp;猫です');
    expect(result.text).toBe('これは 猫です');
  });
});

describe('locateWord', () => {
  const locate = (html: string, candidates: string[]) => locateWord(extractSentenceFromField(html), candidates);

  it('prefers explicit markup over a form match', () => {
    expect(locate('猫がいる。<b>犬</b>もいる', ['猫'])).toEqual([5, 6]);
  });

  it('falls through when the markup covers most of the sentence', () => {
    expect(locate('<b>これは猫です</b>', ['猫'])).toEqual([3, 4]);
  });

  it('matches an alternative writing of the same word', () => {
    expect(locate('毎日たべる', ['食べる', '喰べる', 'たべる'])).toEqual([2, 5]);
  });

  it('matches katakana against a hiragana form', () => {
    expect(locate('お茶にトドメを刺す', ['とどめ'])).toEqual([3, 6]);
  });

  it('matches a conjugated verb by its stem', () => {
    expect(locate('パンを食べたかった', ['食べる'])).toEqual([3, 9]);
  });

  it('matches a conjugated i-adjective by its stem', () => {
    expect(locate('とても面白かった', ['面白い'])).toEqual([3, 8]);
  });

  it('refuses a one-kana stem so する cannot swallow the sentence', () => {
    expect(locate('これは勉強になる', ['する'])).toBeNull();
  });

  it('returns null when the word is absent', () => {
    expect(locate('これは犬です', ['猫'])).toBeNull();
  });
});

describe('truncateAroundWord', () => {
  it('leaves a short sentence untouched', () => {
    expect(truncateAroundWord('これは猫です', 3, 4)).toEqual({ text: 'これは猫です', start: 3, end: 4 });
  });

  it('keeps the clause holding the word and drops the others', () => {
    const filler = 'あ'.repeat(200);
    const text = `${filler}。猫がいる。${filler}`;
    const result = truncateAroundWord(text, filler.length + 1, filler.length + 2)!;

    expect(result.text).toBe('猫がいる。');
    expect(result.text.slice(result.start, result.end)).toBe('猫');
  });

  it('windows a single long clause and marks both cuts', () => {
    const text = 'あ'.repeat(100) + '猫' + 'い'.repeat(100);
    const result = truncateAroundWord(text, 100, 101)!;

    expect(result.text.startsWith('…')).toBe(true);
    expect(result.text.endsWith('…')).toBe(true);
    expect(result.text.slice(result.start, result.end)).toBe('猫');
    expect(result.text.length).toBeLessThanOrEqual(SENTENCE_MAX_LENGTH - 4);
  });

  it('refuses a marked span that cannot fit on its own', () => {
    const text = 'あ'.repeat(200);
    expect(truncateAroundWord(text, 0, 200)).toBeNull();
  });
});

describe('buildSentenceForImport', () => {
  it('produces storable marked text', () => {
    expect(marked('パンを<b>食べた</b>。', ['食べる'])).toBe('パンを**食べた**。');
  });

  it('marks a conjugated verb found by stem', () => {
    expect(marked('毎朝パンを食べたかった。', ['食べる'])).toBe('毎朝パンを**食べたかった**。');
  });

  it('never exceeds the column length', () => {
    const html = 'あ'.repeat(300) + '<b>猫</b>' + 'い'.repeat(300);
    const result = buildSentenceForImport(html, ['猫']);

    expect(result.text!.length).toBeLessThanOrEqual(SENTENCE_MAX_LENGTH);
    expect(result.truncated).toBe(true);
    expect(result.text).toContain('**猫**');
  });

  it('skips a field that is only the word itself', () => {
    expect(buildSentenceForImport('猫', ['猫']).skipped).toBe('empty');
  });

  it('skips a sentence whose word cannot be located', () => {
    expect(buildSentenceForImport('これは犬でした。', ['猫']).skipped).toBe('noHighlight');
  });

  it('marks exactly one occurrence when the word repeats', () => {
    const result = marked('猫がいて猫がいる。', ['猫'])!;
    expect(result.match(/\*\*/g)).toHaveLength(2);
  });
});

describe('storage invariants', () => {
  // The column is varchar(150) and the API rejects text without exactly one marked span, so every
  // shape of input has to come out under the cap with its markers intact or be skipped outright.
  const alphabet = ['あ', 'い', '猫', '。', '、', '！', 'の', 'ア', ' ', '「', '」'];

  function pseudoRandom(seed: number) {
    let state = seed;
    return () => {
      state = (state * 1103515245 + 12345) % 2147483648;
      return state / 2147483648;
    };
  }

  it('never emits text over the column length, for any shape of sentence', () => {
    const random = pseudoRandom(42);

    for (let iteration = 0; iteration < 500; iteration++) {
      const length = 1 + Math.floor(random() * 400);
      let text = '';
      for (let i = 0; i < length; i++) text += alphabet[Math.floor(random() * alphabet.length)];

      const at = Math.floor(random() * text.length);
      const wordLength = 1 + Math.floor(random() * 8);
      const word = text.slice(at, at + wordLength);
      if (!word.trim()) continue;

      const result = buildSentenceForImport(text, [word]);
      if (!result.text) {
        expect(result.skipped).toBeTruthy();
        continue;
      }

      expect(result.text.length).toBeLessThanOrEqual(SENTENCE_MAX_LENGTH);
      expect(result.text.match(/\*\*/g)).toHaveLength(2);
      // A marker pair the API's regex would not accept is as bad as no marker at all.
      expect(/\*\*[^*]+\*\*/.test(result.text)).toBe(true);
    }
  });
});

describe('foldKana', () => {
  it('folds katakana to hiragana without changing length', () => {
    expect(foldKana('トドメ')).toBe('とどめ');
    expect(foldKana('猫カフェ')).toHaveLength(4);
  });
});
