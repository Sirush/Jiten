export function isTextNsfw(text: string): boolean {
  const nsfwWords = [
    '膣壁',
    '子宮',
    '中出し',
    '膣内',
    '射精',
    'セックス',
    '精液',
    '亀頭',
    '肉棒',
    'おちんちん',
    'おまんこ',
    '尿道',
    '液体',
    'ちんぽ',
    'レイプ',
    '中だし',
    'なかだし',
    'オナニー',
    'おっぱい',
    '精子',
    '下着',
    'フェラ',
  ];
  return nsfwWords.some((word) => text.includes(word));
}
