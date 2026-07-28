---
title: Reading anywhere with Jiten Reader
summary: Use the browser extension to parse Japanese on any page, look up words, and see i+1 highlighting.
category: "Using Jiten"
level: beginner
order: 40
icon: material-symbols-light:extension-outline
draft: false
updated: 2026-07-28
---

[Jiten Reader](/reader) is a free, open-source browser extension that brings Jiten's parsing and dictionary to any web page. You can read Japanese anywhere on the web—a news article, a manga reader, a texthooker page—and get instant lookups, word colouring based on what you know, and one-click reviews, all synced with your Jiten account.

## Installing the extension

Jiten Reader is available for [Chrome and other Chromium browsers](https://chromewebstore.google.com/detail/jiten-reader/fkegmlkjkenojfiplaclhlmncfeooaeo) (Edge, Brave, ...) and [Firefox](https://addons.mozilla.org/en-US/firefox/addon/jiten-reader/), including Firefox for Android.

To connect it to your account, grab your **API key** from the bottom of your [settings page](/settings) and paste it into the extension's settings page, which opens automatically after installing. You're all set: the extension can now parse pages and show you your words status.

## Parsing a page

On many popular reading sites the extension parses automatically, such as Ttsu Reader, Mokuro, NHK News Web Easy, Japanese Wikipedia, Satori Reader, asbplayer, common texthooker pages, and more (you can add your own sites in the settings).

Everywhere else, select some Japanese text and either right-click → **Jiten Reader → Parse Selection** or press **Alt+P**.

## Looking up words

Hold **Shift** and hover over any parsed word to open the pop-up. It shows the word's reading, definitions, frequency rank, pitch accents and more.

You can interact with the word from the same pop-up by:
- **Grading it** as an SRS review (Again / Hard / Good / Easy, or pass/fail if you turn on the 2-point grading scale.
- **Mining it** into one of your Jiten study decks along with its sentence.
- **Blacklisting** or marking it as **mastered**.

Your Jiten account will be updated directly, so the vocabulary page and your SRS are always up to date.

## Word colouring and i+1 highlighting

Once a page is parsed, words are coloured by how well you know them: new words stand out (purple by default), words that are due for review are red, young words get an underline, and words you know well (mature or mastered) are left alone so the page stays readable.

The extension can also highlight **i+1 sentences** which are sentences with exactly one unknown word. These are the sweet spot for immersion: you understand everything except the word you're about to learn, so you can try to guess them in context. There's an optional highlight for high-frequency words too, so you can spot the ones most worth mining.

All of the colours and effects are customisable, with a few available themes that you can customise however you want.

::tip
The extension is a great companion to the rest of Jiten: pick a title within the library where you have a good [coverage](/guides/choosing-media-at-your-level), then read it in your favourite reader like Ttsu. Every word you grade or mine there counts everywhere on the site.
::

::note
Jiten Reader is open source ([GitHub](https://github.com/Sirush/JitenReader)), built on top of the excellent [anki-jpdb.reader by Kagu-chan](https://github.com/Kagu-chan/anki-jpdb.reader). Found a bug or want a site supported? Open an [issue](https://github.com/Sirush/JitenReader/issues) or ask on [Discord](https://discord.gg/cZWM7b4wzk).
::
