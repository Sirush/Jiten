<script setup lang="ts">
  const description = 'What Jiten is, who makes it, where the data comes from, and how the project is funded.';

  useSeoMeta({
    title: 'About Jiten',
    description,
    ogTitle: 'About Jiten',
    ogDescription: description,
  });

  defineOgImageComponent('PageOgImage', {
    title: 'About Jiten',
    description: 'A free, open-source platform for learning Japanese through the media you actually want to read and watch.',
  });

  useSchemaOrg([defineWebPage({ '@type': ['AboutPage'] })]);

  const discordUrl = getDiscordLink();
</script>

<template>
  <div class="mx-auto max-w-3xl px-6 py-10 leading-relaxed text-gray-800 dark:text-gray-200">
    <h1 class="mb-6 text-3xl font-bold text-gray-900 dark:text-gray-100">About Jiten</h1>

    <div class="space-y-2">
      <section class="rounded-lg border border-surface-200 bg-surface-0 p-6 dark:border-surface-700 dark:bg-surface-900">
        <p class="mb-4">
          Jiten is a free and open-source platform for Japanaese learning through immersion. The text of thousands of anime, dramas, novels, manga, visual
          novels and other media type is analysed and transformed into data you can use to study: a complete vocabulary list, an estimation of the difficulty, a
          length through the character count. You can study the vocabulary directly on the site in-built SRS with powerful filters to help you choose exactly
          the words you need, and also download then as an Anki deck. By tracking your knowledge on Jiten, you will be able to see your coverage on every title,
          that is how much of it you will understand before even starting.
        </p>
        <p>
          The core idea being Jiten is pretty simple: pick something you want to read or watch, helped by the filters and difficulty rating, then learn the
          words you will encounter right before immersing in it. The <NuxtLink to="/guides/what-is-jiten">what is Jiten guide</NuxtLink> will give you a good
          overview of that method, and the <NuxtLink to="/features">features page</NuxtLink> lists the numerous other features of the website.
        </p>
      </section>

      <section class="rounded-lg border border-surface-200 bg-surface-0 p-6 dark:border-surface-700 dark:bg-surface-900">
        <h2 class="mb-3 text-xl font-semibold text-gray-900 dark:text-gray-100">Who makes it</h2>
        <p>
          Jiten is built and run by a solo developer, Sirus. The full source code is
          <a href="https://github.com/Sirush/Jiten" target="_blank" rel="noopener">available on GitHub</a> under the Apache 2.0 licence, and the main community
          lives on <a :href="discordUrl" target="_blank" rel="noopener">Discord</a>, where feature requests, bug reports and media requests get discussed daily.
          You can find formal company details in the <NuxtLink to="/mentions-legales">legal notice</NuxtLink>.
        </p>
      </section>

      <section class="rounded-lg border border-surface-200 bg-surface-0 p-6 dark:border-surface-700 dark:bg-surface-900">
        <h2 class="mb-3 text-xl font-semibold text-gray-900 dark:text-gray-100">Where the data comes from</h2>
        <p class="mb-4">
          Dictionary data comes from
          <a href="https://www.edrdg.org/wiki/index.php/JMdict-EDICT_Dictionary_Project" target="_blank" rel="noopener">JMdict</a>, JMnedict and KANJIDIC,
          property of the Electronic Dictionary Research and Development Group and used under its licence. The media metadata (covers, titles, descriptions,
          release dates, ...) comes from sources such as AniList, VNDB, TMDB, IGDB and Google Books. They are fully listed in the
          <NuxtLink to="/guides/data-sources">data sources guide</NuxtLink>.
        </p>
        <p class="mb-4">
          Audio pronunciations are generated using <a href="https://voicevox.hiroshiba.jp/" target="_blank" rel="noopener">VOICEVOX</a> with the voices
          <a href="https://zunko.jp/con_ongen_kiyaku.html" target="_blank" rel="noopener">四国めたん</a> (Female 1),
          <a href="https://zunko.jp/con_ongen_kiyaku.html" target="_blank" rel="noopener">九州そら</a> (Female 2, ASMR),
          <a href="https://frontier.creatia.cc/fanclubs/413/posts/4507" target="_blank" rel="noopener">剣崎雌雄</a> (Male 1) and
          <a href="https://virvoxproject.wixsite.com/official/voicevox" target="_blank" rel="noopener">青山龍星</a> (Male 2).
        </p>
        <p>
          Every data derived by Jiten from that analysis including the decks, frequency lists and statistics, is published under
          <a href="https://creativecommons.org/licenses/by-sa/4.0/" target="_blank" rel="noopener">CC BY-SA 4.0</a>. You can use them in your own projects as
          long as you respect the <NuxtLink to="/guides/deck-licensing">licensing guide</NuxtLink> and the
          <NuxtLink to="/guides/api-and-scraping">API terms</NuxtLink>.
        </p>
      </section>

      <section class="rounded-lg border border-surface-200 bg-surface-0 p-6 dark:border-surface-700 dark:bg-surface-900">
        <h2 class="mb-3 text-xl font-semibold text-gray-900 dark:text-gray-100">How it is funded</h2>
        <p>
          Jiten has a fully-fledged core toolset that's continuously being expanded, including: the media library, coverage, deck downloads, the SRS, the various extensions, the statistics, and many more features. There is an optional subscription model in the form of <NuxtLink to="/jiten-plus">Jiten+</NuxtLink> with a few premium features, as well as <NuxtLink to="/donate">donations</NuxtLink>. There are no ads, and your data is never
          sold. You can find all the details in the <NuxtLink to="/privacy">privacy policy</NuxtLink>.
        </p>
      </section>

      <section class="rounded-lg border border-surface-200 bg-surface-0 p-6 dark:border-surface-700 dark:bg-surface-900">
        <h2 class="mb-3 text-xl font-semibold text-gray-900 dark:text-gray-100">Contact</h2>
        <p>
          Email <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>, join the <a :href="discordUrl" target="_blank" rel="noopener">Discord server</a>, or
          open an issue on <a href="https://github.com/Sirush/Jiten/issues" target="_blank" rel="noopener">GitHub</a>.
        </p>
      </section>
    </div>
  </div>
</template>
