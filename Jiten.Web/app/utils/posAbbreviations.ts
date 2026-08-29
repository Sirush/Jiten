const posAbbreviationMap: Record<string, string> = {
  noun: 'n',
  'adverbial noun': 'n-adv',
  'proper noun': 'n-pr',
  'noun, used as a prefix': 'n-pref',
  'noun, used as a suffix': 'n-suf',
  'noun (temporal)': 'n-tmp',
  'adjective (keiyoushi)': 'adj-i',
  'adjective (keiyoushi) - yoi/ii class': 'adj-ix',
  'adjectival nouns or quasi-adjectives (keiyodoshi)': 'adj-na',
  "nouns which may take the genitive case particle 'no'": 'adj-no',
  'noun or verb acting prenominally': 'adj-f',
  'pre-noun adjectival': 'adj-pn',
  "'taru' adjective": 'adj-t',
  "'kari' adjective (archaic)": 'adj-kari',
  "'ku' adjective (archaic)": 'adj-ku',
  "'shiku' adjective (archaic)": 'adj-shiku',
  'archaic/formal form of na-adjective': 'adj-nari',
  'adverb (fukushi)': 'adv',
  "adverb taking the 'to' particle": 'adv-to',
  'Ichidan verb': 'v1',
  'Ichidan verb - kureru special class': 'v1-s',
  'Godan verb - -aru special class': 'v5aru',
  "Godan verb with 'bu' ending": 'v5b',
  "Godan verb with 'gu' ending": 'v5g',
  "Godan verb with 'ku' ending": 'v5k',
  'Godan verb - Iku/Yuku special class': 'v5k-s',
  "Godan verb with 'mu' ending": 'v5m',
  "Godan verb with 'nu' ending": 'v5n',
  "Godan verb with 'ru' ending": 'v5r',
  "Godan verb with 'ru' ending (irregular verb)": 'v5r-i',
  "Godan verb with 'su' ending": 'v5s',
  "Godan verb with 'tsu' ending": 'v5t',
  "Godan verb with 'u' ending": 'v5u',
  "Godan verb with 'u' ending (special class)": 'v5u-s',
  'Godan verb - Uru old class verb (old form of Eru)': 'v5uru',
  'Kuru verb - special class': 'vk',
  'noun or participle which takes the aux. verb suru': 'vs',
  'suru verb - included': 'vs-i',
  'suru verb - special class': 'vs-s',
  'su verb - precursor to the modern suru': 'vs-c',
  'Ichidan verb - zuru verb (alternative form of -jiru verbs)': 'vz',
  'intransitive verb': 'vi',
  'transitive verb': 'vt',
  'irregular nu verb': 'vn',
  'irregular ru verb, plain form ends with -ri': 'vr',
  'verb unspecified': 'v',
  auxiliary: 'aux',
  'auxiliary verb': 'aux-v',
  'auxiliary adjective': 'aux-adj',
  conjunction: 'conj',
  copula: 'cop',
  counter: 'ctr',
  'expressions (phrases, clauses, etc.)': 'expr',
  'interjection (kandoushi)': 'intj',
  particle: 'prt',
  prefix: 'pref',
  suffix: 'suf',
  pronoun: 'pn',
  numeric: 'num',
  'usually written using kana': 'uk',
  'onomatopoeic or mimetic': 'on-mim',
  abbreviation: 'abbr',
  archaic: 'arch',
  colloquial: 'col',
  'dated term': 'dated',
  derogatory: 'derog',
  'familiar language': 'fam',
  'female term or language': 'fem',
  'formal or literary term': 'form',
  'historical term': 'hist',
  'honorific or respectful (sonkeigo)': 'hon',
  'humble (kenjougo)': 'hum',
  'idiomatic expression': 'id',
  'jocular, humorous term': 'joc',
  'male term or language': 'male',
  'manga slang': 'm-sl',
  'Internet slang': 'net-sl',
  'obsolete term': 'obs',
  'polite (teineigo)': 'pol',
  'poetical term': 'poet',
  'rare term': 'rare',
  sensitive: 'sens',
  slang: 'sl',
  vulgar: 'vulg',
  'rude or X-rated term (not displayed in educational software)': 'X',
  yojijukugo: 'yoji',
  proverb: 'prov',
  quotation: 'quote',
};

export function abbreviatePos(pos: string): string {
  const lower = pos.toLowerCase();
  for (const [key, abbr] of Object.entries(posAbbreviationMap)) {
    if (key.toLowerCase() === lower) return abbr;
  }
  return pos;
}

// Canonical display order — JMdict's per-sense POS order is editor-entered and inconsistent
// (e.g. [adj-no, n] on one sense, [n, adj-no] on the next). Sort by position in the map above
// (noun → adjective → adverb → verb → …) so it reads consistently and the POS header dedupes.
const posOrderKeys = Object.keys(posAbbreviationMap).map((k) => k.toLowerCase());
export function posSortKey(pos: string): number {
  const i = posOrderKeys.indexOf(pos.toLowerCase());
  return i === -1 ? posOrderKeys.length : i;
}

export function sortPos(pos: string[]): string[] {
  return [...pos].sort((a, b) => posSortKey(a) - posSortKey(b));
}

const posTypeColors: Record<string, string> = {
  n: 'blue',
  v: 'green',
  adj: 'purple',
  adv: 'amber',
  expr: 'teal',
  prt: 'gray',
  misc: 'gray',
};

export function posColorClass(abbr: string): string {
  if (abbr.startsWith('v')) return posTypeColors.v;
  if (abbr.startsWith('adj')) return posTypeColors.adj;
  if (abbr.startsWith('adv')) return posTypeColors.adv;
  if (abbr.startsWith('n')) return posTypeColors.n;
  if (abbr === 'expr' || abbr === 'intj') return posTypeColors.expr;
  if (abbr === 'prt' || abbr === 'conj' || abbr === 'cop') return posTypeColors.prt;
  return posTypeColors.misc;
}

// JMdict <misc> entity codes. The API ships these raw (unlike pos/field/dial, which it expands
// server-side), so the badge shows the code and this table supplies the tooltip. Wording is
// shortened from JMdict's own phrasing where the full text reads badly in a badge tooltip.
const miscLabelMap: Record<string, string> = {
  uk: 'usu. kana',
  abbr: 'abbreviation',
  'on-mim': 'onomatopoeia',
  yoji: 'yojijukugo',
  joc: 'jocular',
  'net-sl': 'net slang',
  'm-sl': 'manga slang',
  sl: 'slang',
  col: 'colloquial',
  hon: 'honorific',
  hum: 'humble',
  pol: 'polite',
  fam: 'familiar',
  derog: 'derogatory',
  vulg: 'vulgar',
  sens: 'sensitive',
  dated: 'dated',
  hist: 'historical',
  obs: 'obsolete',
  rare: 'rare',
  arch: 'archaic',
  poet: 'poetical',
  chn: "children's",
  fem: 'female term',
  male: 'male term',
  proverb: 'proverb',
  id: 'idiomatic',
  euph: 'euphemistic',
  X: 'X-rated',
  form: 'formal or literary term',
  quote: 'quotation',
  char: 'character',
  company: 'company name',
  creat: 'creature',
  dei: 'deity',
  doc: 'document',
  ev: 'event',
  fict: 'fiction',
  given: 'given name',
  group: 'group',
  leg: 'legend',
  myth: 'mythology',
  obj: 'object',
  organization: 'organization name',
  oth: 'other',
  person: 'personal name',
  place: 'place name',
  product: 'product name',
  relig: 'religion',
  serv: 'service',
  ship: 'ship name',
  station: 'railway station',
  surname: 'family name',
  unclass: 'unclassified name',
  work: 'work of art',
};

export const miscCodes = Object.keys(miscLabelMap);

export function miscLabel(misc: string): string {
  return miscLabelMap[misc] ?? misc;
}
