<script setup lang="ts">
  import { ref } from 'vue';
  import { useHead } from '#imports';
  import { getDiscordLink } from '~/utils/getDiscordLink';

  const faqData = ref([
    {
      question: 'What can I use the decks for?',
      answer: `Decks, frequency lists, and other derived statistical data are licensed under <a href="https://creativecommons.org/licenses/by-sa/4.0/" target="_blank" rel="noopener noreferrer">CC BY-SA 4.0</a>. You're free to use, share, and adapt them for any purpose — including commercial — as long as you give attribution and share any adaptations under the same license.<br /><br />This does not cover example sentences or media text, which belong to their respective copyright holders.`,
    },
    {
      question: 'Can I use your API or scrape the site?',
      answer: `The API endpoints are publicly available and you're welcome to use them for personal projects, small-scale research, or tools that complement the learning experience. However, please don't bulk-scrape, crawl, or systematically download the site's data — for example, dumping every deck's vocabulary to mirror the database or build a competing service.<br /><br />
      In short:
      <ul class="list-disc pl-8">
        <li>Personal projects, research, and hobby tools — go for it</li>
        <li>Bulk downloading or mirroring significant portions of the database — not allowed</li>
        <li>Redistributing scraped data commercially — not allowed</li>
      </ul><br />
      The API may change at any time without notice and is rate-limited. If you're unsure whether your use case is okay, feel free to ask on <a href="${getDiscordLink()}" target="_blank" rel="noopener noreferrer">Discord</a>.`,
    },
    {
      question: 'Why is X missing?',
      answer: `While I aim to make the website as complete as possible, it's an arduous process and I have to prioritise some media over others. If you'd like something added, you can submit a request on the <a href="/requests">requests page</a> after creating an account. Requests are fulfilled depending on availability to source the text.`,
    },
    {
      question: 'How accurate are the media decks?',
      answer: `For VNs: Extracting text from VNs is a manual process. The decks should be fairly accurate, but they might contain extra text depending on the engine. Please report anything that seems off. <br />For books: Books are extracted semi-automatically, and may still contain extra info like the table of contents, please be aware of it. <br />For anime/movies/dramas: The decks are based on subtitle files, extracted in a semi-automated way. The subtitles may contain errors. There's also a chance the episode count, the episode numbers, etc, are not accurate. If you find any mistakes, please use the <b>report an issue</b> button.`,
    },
    {
      question: 'Why is the word count or character count different from another website?',
      answer: `The text extraction process is custom-built and may differ from other websites.. The character count should be mostly the same, unless there's data that's missing or data that was erroneously included. For example, a game can contain item text in another file that was forgotten to be included. The word count can have more differences as there's different ways the words can be split, for example, due to differences in word segmentation. If you find anything that looks erroneous, please use the <b>report an issue</b> button.`,
    },
    {
      question: 'Which websites do you source the information from?',
      answer: `For the metadata, these APIs are used: <br />
            Some metadata (descriptions, tags, and ratings) is derived from <a href="https://vndb.org" target="_blank" rel="noopener noreferrer">VNDB</a>, licensed under the <a href="https://opendatacommons.org/licenses/odbl/1-0/" target="_blank" rel="noopener noreferrer">Open Database License (ODbL) 1.0.</a>
      <a href="https://www.themoviedb.org" target="_blank" rel="noopener noreferrer"><img src="/img/tmdb.svg" alt="tmdb" width="200px" /></a>
      <div class="text-gray-500 text-xs" style="margin-top: 4px;">This product uses the TMDB API but is not endorsed or certified by TMDB</div>
      <div><a href="https://anilist.co" target="_blank" rel="noopener noreferrer">AniList</a></div>
      <div><a href="https://igdb.com" target="_blank" rel="noopener noreferrer">IGDB</a></div>
      <div><a href="https://www.google.com/books" target="_blank" rel="noopener noreferrer">Google Books</a></div>
      <br />
      The sources for the data are: <br />
      Subtitles from <a href="https://www.jimaku.cc" target="_blank" rel="noopener noreferrer">Jimaku</a> <br />
      Game scripts from <a href="https://sites.google.com/view/jo-mako/resources/resources-list?authuser=0" target="_blank" rel="noopener noreferrer">Jo-Mako</a> <br />
      Some VN scripts from <a href="http://wiki.wareya.moe/Stats" target="_blank" rel="noopener noreferrer">Wareya</a>.<br />
      Everything else comes from me or our generous contributors on <a href="${getDiscordLink()}" target="_blank" rel="noopener noreferrer">Discord</a>!<br /><br />
      Audio pronunciations are generated using <a href="https://voicevox.hiroshiba.jp/" target="_blank" rel="noopener noreferrer">VOICEVOX</a> with voices <a href="https://zunko.jp/con_ongen_kiyaku.html" target="_blank" rel="noopener noreferrer">四国めたん</a> (Female 1), <a href="https://zunko.jp/con_ongen_kiyaku.html" target="_blank" rel="noopener noreferrer">九州そら</a> (Female 2, ASMR), <a href="https://frontier.creatia.cc/fanclubs/413/posts/4507" target="_blank" rel="noopener noreferrer">剣崎雌雄</a> (Male 1), and <a href="https://virvoxproject.wixsite.com/official/voicevox" target="_blank" rel="noopener noreferrer">青山龍星</a> (Male 2).`,
    },
    {
      question: 'Will you support more media types in the future?',
      answer: `Yes, I plan to support YouTube. <br />
      Each comes with its own set of challenges, which is why they will take more time. If you have any other suggestions, please let me know on <a href="${getDiscordLink()}" target="_blank" rel="noopener noreferrer">Discord</a>.`,
    },
    {
      question: 'Will the website ever be paid?',
      answer: `The core features—including access to the decks and the future SRS—will always be free. There might be some premium, extra features in the future, reserved to supporters, but I want the most important features to be accessible to all.`,
    },
  ]);

  const stripHtml = (html: string): string => {
    if (!html) return '';
    const plainText = html.replace(/<[^>]*>/g, '');

    return plainText.replace(/\s+/g, ' ').trim();
  };

  // 2. Generate the JSON-LD structured data
  const generateFaqSchema = () => {
    return {
      '@context': 'https://schema.org',
      '@type': 'FAQPage',
      mainEntity: faqData.value.map((item) => ({
        '@type': 'Question',
        name: item.question,
        acceptedAnswer: {
          '@type': 'Answer',
          text: stripHtml(item.answer),
        },
      })),
    };
  };

  const faqSchemaJson = computed(() => JSON.stringify(generateFaqSchema()));

  useHead({
    script: [
      {
        type: 'application/ld+json',
        textContent: faqSchemaJson.value,
      },
    ],
    title: 'FAQ',
    meta: [{ name: 'description', content: 'Frequently asked questions about the decks and the website.' }],
  });
</script>

<template>
  <div class="flex flex-col gap-2">
    <Panel v-for="(faqItem, index) in faqData" :key="index" :header="faqItem.question">
      <div v-html="sanitiseHtml(faqItem.answer)"></div>
    </Panel>
  </div>
</template>

<style scoped></style>
