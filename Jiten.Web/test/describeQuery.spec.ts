import { describe, expect, it } from 'vitest';
import { looksLikeDescription, readDescribeQuery } from '~/utils/describeQuery';

describe('looksLikeDescription', () => {
  it('treats short or title-like text as a title', () => {
    for (const title of [
      'Naruto',
      'Steins;Gate',
      'Fate stay night',
      '君の名は',
      '涼宮ハルヒの憂鬱',
      'とある魔術の禁書目録',
      'Clannad After Story',
      '「一緒に寝たいんですよね、せんぱい？」と甘くささやかれて今夜も眠れない',
      'この素晴らしい世界に祝福を！',
      '魔法少女まどか☆マギカ',
    ]) {
      expect(looksLikeDescription(title), title).toBe(false);
    }
  });

  it('treats sentences as descriptions', () => {
    for (const text of [
      'a visual novel about ninja',
      'slow-burn romance in a rural town',
      'detective solving murders in a small town',
      '探偵もの、主人公がアホ',
      '田舎町でゆっくり進む恋愛',
      '幽霊が見える孤独な少女の話',
    ]) {
      expect(looksLikeDescription(text), text).toBe(true);
    }
  });
});

describe('readDescribeQuery', () => {
  it('reads a trimmed string from the route and ignores stubs', () => {
    expect(readDescribeQuery('  cozy ghost story ')).toBe('cozy ghost story');
    expect(readDescribeQuery(['first', 'second'])).toBe('first');
    expect(readDescribeQuery('a')).toBeNull();
    expect(readDescribeQuery(undefined)).toBeNull();
  });
});
