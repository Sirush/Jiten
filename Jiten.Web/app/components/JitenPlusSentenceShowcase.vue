<script setup lang="ts">
  import { MediaType, type SentenceProjectionPoint } from '~/types';
  import { sentenceRubyHtml } from '~/utils/sentenceRuby';

  const store = useJitenStore();

  const TOTAL_SENTENCES = 4800;
  const START = { readable: 1824, oneUnknown: 1008, twoUnknown: 816, threeOrMoreUnknown: 1152 };

  // Learning a word moves `unlocks` sentences from i+1 to i+0, `fromTwo` from i+2 to i+1 and `fromThree` from i+3 to i+2.
  const learnNext = [
    { key: 'kekkyoku', word: '結局', reading: 'けっきょく', meaning: 'in the end, after all', unlocks: 142, fromTwo: 96, fromThree: 60 },
    { key: 'katte', word: '勝手', reading: 'かって', meaning: 'as one pleases, selfish', unlocks: 118, fromTwo: 80, fromThree: 51 },
    { key: 'aikawarazu', word: '相変わらず', reading: 'あいかわらず', meaning: 'as usual, as ever', unlocks: 87, fromTwo: 62, fromThree: 40 },
    { key: 'yudan', word: '油断', reading: 'ゆだん', meaning: 'letting your guard down', unlocks: 64, fromTwo: 45, fromThree: 31 },
  ];

  const known = ref(new Set<string>());

  function toggleKnown(key: string) {
    const next = new Set(known.value);
    if (!next.delete(key)) next.add(key);
    known.value = next;
  }

  const counts = computed(() => {
    const c = { ...START };
    for (const w of learnNext) {
      if (!known.value.has(w.key)) continue;
      c.readable += w.unlocks;
      c.oneUnknown += w.fromTwo - w.unlocks;
      c.twoUnknown += w.fromThree - w.fromTwo;
      c.threeOrMoreUnknown -= w.fromThree;
    }
    return c;
  });

  const projection: SentenceProjectionPoint[] = [
    { words: 0, greedy: 1824, byFrequency: 1824 },
    { words: 50, greedy: 2350, byFrequency: 2020 },
    { words: 100, greedy: 2710, byFrequency: 2230 },
    { words: 150, greedy: 2980, byFrequency: 2430 },
    { words: 200, greedy: 3190, byFrequency: 2610 },
    { words: 250, greedy: 3360, byFrequency: 2780 },
    { words: 300, greedy: 3500, byFrequency: 2940 },
    { words: 350, greedy: 3615, byFrequency: 3080 },
    { words: 400, greedy: 3710, byFrequency: 3210 },
  ];

  const TARGET = '約束';

  function sentence(text: string, unknownWords: string[], mediaType: MediaType) {
    const unknownSpans = unknownWords.map((w) => ({ position: text.indexOf(w), length: w.length }));
    return {
      text,
      html: sentenceRubyHtml(text, text.indexOf(TARGET), TARGET.length, [], { unknownSpans }),
      unknown: unknownWords.length,
      source: getMediaTypeText(mediaType),
    };
  }

  const sentencesByLevel = [
    [
      sentence('約束は守るよ。', [], MediaType.Anime),
      sentence('明日また会うって約束したよね。', [], MediaType.VisualNovel),
      sentence('じゃあ、約束だよ。', [], MediaType.Drama),
      sentence('約束があるから、先に帰るね。', [], MediaType.Anime),
    ],
    [
      sentence('その約束を破るつもりはなかった。', ['破る'], MediaType.Novel),
      sentence('彼女との約束をすっかり忘れていた。', ['すっかり'], MediaType.Anime),
      sentence('約束通り、駅前で待ち合わせた。', ['待ち合わせた'], MediaType.VisualNovel),
      sentence('約束を守れなかったことを、ずっと後悔している。', ['後悔している'], MediaType.Drama),
    ],
    [
      sentence('約束の時間に遅刻して、先輩に叱られた。', ['遅刻', '叱られた'], MediaType.Drama),
      sentence('幼い頃に交わした約束を、今でも覚えている。', ['幼い', '交わした'], MediaType.Novel),
      sentence('果たせなかった約束が、ずっと胸に引っかかっている。', ['果たせなかった', '引っかかっている'], MediaType.VisualNovel),
      sentence('軽々しく約束するなと、祖父によく諭された。', ['軽々しく', '諭された'], MediaType.Anime),
    ],
  ];

  const levelOptions = [
    { label: 'i+1', value: 0 },
    { label: 'i+2', value: 1 },
    { label: 'i+3', value: 2 },
  ];
  const level = ref(0);
  const shownSentences = computed(() => sentencesByLevel[level.value] ?? []);
  const levelHint = computed(() => {
    if (level.value === 0) return `You know every word except ${TARGET}.`;
    return `${level.value === 1 ? 'One other word is' : 'Two other words are'} new to you, marked in blue.`;
  });
</script>

<template>
  <section class="band bg-primary-50 dark:bg-primary-950/30 border-y border-primary-100 dark:border-primary-900/60">
    <div class="max-w-5xl mx-auto px-4 py-10 md:py-12">
      <div class="text-center max-w-4xl mx-auto">
        <h2 class="text-2xl md:text-3xl font-bold text-gray-900 dark:text-white">Know which sentences you can understand</h2>
        <p class="mt-3 text-gray-700 dark:text-gray-300 leading-relaxed">
          With Jiten+, the sentence of every title will be matched against the words you know. You'll be able to see how many of them you can understand today, those that have a single unknown words (i+1) up to 3 unknowns, study by the words that will unlock the most sentences for you first and find sentences at your level.
        </p>
      </div>

      <div class="mt-10 grid gap-8 md:gap-6 md:grid-cols-2">
        <div class="demo flex flex-col ring-2 ring-primary/40 bg-white dark:bg-gray-900">
          <span class="demo__tab bg-primary text-primary-contrast">Example</span>
          <h3 class="demo__title text-gray-900 dark:text-white">Sentence stats for every title</h3>
          <p class="demo__lede text-gray-600 dark:text-gray-300">An anime you're watching with {{ TOTAL_SENTENCES.toLocaleString() }} sentences</p>

          <div class="mt-4 flex flex-col gap-4">
            <SentenceReadableHeadline :readable="counts.readable" :one-unknown="counts.oneUnknown" :total="TOTAL_SENTENCES" />
            <SentenceDistributionBar
              :readable="counts.readable"
              :one-unknown="counts.oneUnknown"
              :two-unknown="counts.twoUnknown"
              :three-or-more-unknown="counts.threeOrMoreUnknown"
            />
          </div>

          <div class="mt-5">
            <div class="learn-head text-xs text-gray-600 dark:text-gray-400 border-b border-surface-200 dark:border-surface-700">
              <span>Learn these next</span>
              <span>New sentences</span>
            </div>
            <ul>
              <li v-for="w in learnNext" :key="w.key">
                <button
                  type="button"
                  class="learn-row"
                  :class="known.has(w.key) ? 'bg-green-50 dark:bg-green-950/30' : 'hover:bg-surface-50 dark:hover:bg-surface-800/60'"
                  :aria-pressed="known.has(w.key)"
                  @click="toggleKnown(w.key)"
                >
                  <span class="min-w-0 flex flex-col">
                    <span lang="ja" class="text-lg font-medium leading-tight text-gray-900 dark:text-white">
                      <ruby>{{ w.word }}<rp>(</rp><rt>{{ w.reading }}</rt><rp>)</rp></ruby>
                    </span>
                    <span class="text-xs text-gray-600 dark:text-gray-300 truncate">{{ w.meaning }}</span>
                  </span>
                  <span class="text-sm tabular-nums font-semibold text-green-700 dark:text-green-400">+{{ w.unlocks }}</span>
                  <span
                    class="learn-row__state"
                    :class="
                      known.has(w.key)
                        ? 'bg-green-700 dark:bg-green-500 text-white dark:text-gray-950 border-transparent'
                        : 'border-surface-300 dark:border-surface-600 text-gray-700 dark:text-gray-200'
                    "
                  >
                    <Icon :name="known.has(w.key) ? 'material-symbols:check-rounded' : 'material-symbols:add-rounded'" aria-hidden="true" />
                    {{ known.has(w.key) ? 'Known' : 'Mark known' }}
                  </span>
                </button>
              </li>
            </ul>
          </div>

          <p class="demo__foot text-gray-600 dark:text-gray-300">
            You can also see a progression of the sentences you know across episodes and sort the library by readable sentences.
          </p>
        </div>

        <div class="demo flex flex-col ring-2 ring-primary/40 bg-white dark:bg-gray-900">
          <span class="demo__tab bg-primary text-primary-contrast">Example</span>
          <h3 class="demo__title text-gray-900 dark:text-white">Sentences for any word, at your level</h3>
          <p class="demo__lede text-gray-600 dark:text-gray-300">Get sentences from real media, based on the number of words you don't know in them.</p>

          <div class="mt-4 flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <span lang="ja" class="text-3xl font-bold text-gray-900 dark:text-white">
              <ruby>{{ TARGET }}<rp>(</rp><rt class="text-xs font-normal">やくそく</rt><rp>)</rp></ruby>
            </span>
            <span class="text-sm text-gray-600 dark:text-gray-300">promise, appointment</span>
          </div>

          <div class="mt-3 flex flex-wrap items-center gap-x-3 gap-y-2">
            <SelectButton v-model="level" :options="levelOptions" option-label="label" option-value="value" :allow-empty="false" size="small" aria-label="Unknown words" />
            <span class="text-xs text-gray-600 dark:text-gray-300" aria-live="polite">{{ levelHint }}</span>
          </div>

          <ul class="mt-4 flex flex-col gap-2.5">
            <li v-for="s in shownSentences" :key="s.text" class="flex flex-col">
              <blockquote class="border-l-4 border-primary-500 pl-4 pr-3 py-2.5 bg-gray-50 dark:bg-gray-800/70 rounded-r">
                <div class="flex items-start gap-2">
                  <span lang="ja" class="flex-1 text-gray-900 dark:text-gray-100" :class="sentenceSizeClass(store.sentenceSize)" v-html="s.html" />
                  <span class="mt-0.5 h-5 shrink-0 inline-flex items-center">
                    <IPlusOneBadge :unknown="s.unknown" />
                  </span>
                </div>
              </blockquote>
              <span class="mt-1 ml-5 text-xs text-gray-500 dark:text-gray-400">{{ s.source }}</span>
            </li>
          </ul>

          <p class="demo__foot text-gray-600 dark:text-gray-300">
            Filter by media type or by the titles on your list, then favourite a sentence to use it on your card during your studies.
          </p>
        </div>
      </div>

      <div class="demo mt-8 md:mt-6 grid gap-5 md:grid-cols-[2fr_3fr] md:items-center ring-2 ring-primary/40 bg-white dark:bg-gray-900">
        <span class="demo__tab bg-primary text-primary-contrast">Example</span>
        <div>
          <h3 class="demo__title text-gray-900 dark:text-white">Learn words in the order that unlock the most sentences</h3>
          <p class="demo__lede text-gray-600 dark:text-gray-300">
            For every title, the words will be ranked in the order where they make the most sentences readable for you. You will have access to a new deck study order following this.
          </p>
        </div>
        <LazySentenceProjectionChart :points="projection" :total="TOTAL_SENTENCES" hydrate-on-visible />
      </div>

      <slot />
    </div>
  </section>
</template>

<style scoped>
  .band {
    width: 100vw;
    margin-left: calc(50% - 50vw);
  }

  .demo {
    position: relative;
    border-radius: var(--radius-xl);
    padding: 1.25rem;
  }

  .demo__tab {
    position: absolute;
    top: -0.65rem;
    right: 1rem;
    border-radius: 9999px;
    padding: 0.1rem 0.6rem;
    font-size: 0.75rem;
    font-weight: 600;
  }

  .demo__title {
    font-size: 1.1rem;
    font-weight: 700;
    line-height: 1.35;
  }

  .demo__lede {
    margin-top: 0.3rem;
    font-size: 0.875rem;
    line-height: 1.5;
  }

  .demo__foot {
    margin-top: auto;
    padding-top: 1rem;
    font-size: 0.8rem;
    line-height: 1.5;
  }

  /* Right padding matches the row's state column, gap and padding, so the label ends above the counts. */
  .learn-head {
    display: flex;
    justify-content: space-between;
    gap: 0.75rem;
    padding: 0 7.75rem 0.4rem 0.5rem;
    white-space: nowrap;
  }

  .learn-row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto 6.5rem;
    gap: 0.75rem;
    width: 100%;
    padding-inline: 0.5rem;
    align-items: center;
    min-height: 3.25rem;
    padding-block: 0.4rem;
    border-radius: var(--radius-md);
    text-align: left;
    cursor: pointer;
    transition: background-color 0.2s;
  }

  .learn-row:focus-visible {
    outline: 2px solid var(--p-primary-500);
    outline-offset: 2px;
  }

  .learn-row__state {
    display: inline-flex;
    align-items: center;
    gap: 0.2rem;
    justify-content: center;
    border-width: 1px;
    border-radius: var(--radius-md);
    padding: 0.2rem 0.5rem;
    font-size: 0.75rem;
    font-weight: 600;
    white-space: nowrap;
    transition:
      background-color 0.2s,
      color 0.2s;
  }
</style>
