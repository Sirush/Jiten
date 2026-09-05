/**
 * Example description queries shown under the home search
 */
export const DESCRIBE_EXAMPLES = [
  'cozy ghost story',
  'detective drama in a small town',
  'a lonely girl who can see ghosts',
  'time loop where the protagonist keeps dying',
  'cooking competition anime',
  'salaryman reincarnated in another world',
  'yakuza movie about loyalty and betrayal',
  'psychological horror in an apartment building',
  "an anime about a girls' rock band",
  'medical drama set in an emergency room',
  'vampire romance',
  'survival game where students are forced to kill each other',
  'a drama about a bank employee exposing corruption',
  'heartwarming story about raising a child alone',
  'a visual novel set in a school for witches',
  'a visual novel about a girl who lives with a ghost',
  'manga about a high school volleyball team',
  'manga about a girl who transfers to a school full of delinquents',
  'a mystery novel set in a second-hand bookstore',
  'light novel about a hero who is far too overpowered',
  'video game where you explore a fantasy world with a sword',
  'a video game about high school students with supernatural powers',
  'anime about giant robots',
  'high school baseball anime',
  'drama about a lawyer who never loses',
  '田舎町でゆっくり進む恋愛',
  '探偵もの、主人公がアホ',
  '銀行員が不正を暴く社会派ドラマ',
  '戦国時代を舞台にした武将の物語',
  '部活で全国大会を目指す高校生のアニメ',
  '異世界に転生して無双する小説',
  '会社を辞めて田舎で農業を始める話',
  '人生をやり直すために過去に戻る',
  '料理で人を幸せにする話',
];

export function pickDescribeExamples(seed: number, count = 3): string[] {
  const pool = [...DESCRIBE_EXAMPLES];
  const picked: string[] = [];
  let state = seed >>> 0 || 1;
  while (picked.length < count && pool.length > 0) {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    picked.push(pool.splice(state % pool.length, 1)[0]!);
  }
  return picked;
}
