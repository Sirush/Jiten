<script setup lang="ts"></script>

<template>
  <div class="flex flex-col gap-2">
    <Panel header="What can I use the decks for?">
      Feel free to use them for anything you want, but I'll respectfully ask you to not scrape them to start a competing
      website. If you redistribute the decks, I'd appreciate a linkback to the website! :)
    </Panel>

    <Panel header="Why is X missing?">
      While I aim to make the website the most complete it can be, it's an arduous process and I have to prioritise some
      media over others. If you have a specific request, please let me know on
      <a :href="getDiscordLink()" target="_blank">Discord</a>, and it'll be added depending on the availability to
      source the text.
    </Panel>

    <Panel header="How accurate are the media decks?">
      For VNs: Extracting text from VNs is a manual process. The decks should be fairly accurate, but they might contain
      extra text depending on the engine. Please report anything that seems off. <br />
      For books: Books are extracted semi-automatically, and may still contain extra info like the table of contents,
      please be aware of it. <br />
      For anime/movies/dramas: The decks are based on subtitle files, extracted in a semi-automated way. The subtitles
      may contain errors. There's also a chance the episode count, the episode numbers, etc, are not accurate. If you
      find any mistakes, please let me know on <a :href="getDiscordLink()" target="_blank">Discord</a>.
    </Panel>

    <Panel header="Why is the word count or character count different from another website?">
      The text extraction process is custom-built and may differ from other websites.. The character count should be
      mostly the same, unless there's data that's missing or data that was erroneously included. For example, a game can
      contain item text in another file that was forgotten to be included. The word count can have more differences as
      there's different ways the words can be split, for example, due to differences in word segmentation. If you find
      anything that looks erroneous, please let me know on <a :href="getDiscordLink()" target="_blank">Discord</a>.
    </Panel>

    <Panel header="Which websites do you source the information from?">
      For the metadata, these APIs are used: <br />
      <a href="https://vndb.org" target="_blank">VNDB</a>
      <a href="https://www.themoviedb.org" target="_blank"><img src="/img/tmdb.svg" alt="tmdb" width="200px" /></a>
      <div class="text-gray-500 text-xs">This product uses the TMDB API but is not endorsed or certified by TMDB</div>
      <div><a href="https://anilist.co" target="_blank">Anilist</a></div>
      <div><a href="https://igdb.com" target="_blank">IGDB</a></div>
      <div><a href="https://www.google.com/books" target="_blank">Google Books</a></div>

      <br />
      The sources for the data are: <br />
      Subtitles from <a href="https://www.jimaku.cc" target="_blank">Jimaku</a> <br />
      Game scripts from
      <a href="https://sites.google.com/view/jo-mako/resources/resources-list?authuser=0" target="_blank">Jo-Mako</a>
      <br />
      Some VN scripts from <a href="http://wiki.wareya.moe/Stats" target="_blank">Wareya</a>.<br />
      Everything else comes from me or our generous contributors on
      <a :href="getDiscordLink()" target="_blank">Discord</a>:
      Zakamutt, 人木, Zwansanwan, Rock, Usagi
    </Panel>

    <Panel header="What's next for the website?">
      For a more detailed and up-to-date roadmap, please check out the
      <a :href="getDiscordLink()" target="_blank">Discord</a>.<br />
      But in general, the next steps are, in no particular order:
      <ul class="list-disc pl-8">
        <li>Add more decks, always</li>
        <li>A user system</li>
        <li>Add a complete SRS system integrated to the website</li>
        <li>Support for names and custom names</li>
        <li>Add a way for users to add their own, custom decks</li>
        <li>Add a way for users to submit media (which also means a moderation queue for them)</li>
      </ul>
    </Panel>

    <Panel header="Will you support more media types in the future?">
      Yes, I plan to support YouTube and manga. <br />
      Each comes with its own set of challenges, which is why they will take more time. If you have any other
      suggestions, please let me know on <a :href="getDiscordLink()" target="_blank">Discord</a>.
    </Panel>

    <Panel header="Will the website ever be paid?">
      The core features—including access to the decks and the future SRS—will always be free. There might be some
      premium, extra features in the future, reserved to supporters, but I want the most important features to be
      accessible to all.
    </Panel>

    <Panel header="Can I use your API endpoints?">
      You can use them as they are publicly available, but be aware that they may change at any time without notice.
      <br />They can also be rate-limited to avoid overloading the servers.
    </Panel>
  </div>
</template>

<style scoped></style>
