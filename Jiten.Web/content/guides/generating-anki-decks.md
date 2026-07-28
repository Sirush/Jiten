---
title: Generating and importing Anki decks
summary: Export a vocabulary deck from any title with only the words you want t ostudy
category: Studying
level: beginner
order: 20
icon: material-symbols-light:cards-star-outline
draft: false
updated: 2026-07-28
---

Any title in the library can be turned into an Anki deck and you can filter it to only get the words you actually want to study. While you don't need an account to download a deck, you will need one if you want to keep track of the words you know to exclude them from the download.

You can **Download / Learn** on any media page to open the dialog.

## Choosing a format

**Anki** will produce an `.apkg` built on the community [Lapis](https://github.com/donkuri/lapis) template that you can directly import inside Anki. Other formats are available:

| Format | What you get |
|---|---|
| **Text** | One word per line, nothing else |
| **Text (Rep)** | The same, with each word repeated once per occurrence |
| **CSV** | Nine columns: the word, its furigana and kana, occurrences, frequency, pitch, definitions, an example sentence and its dictionary id |
| **Yomitan** | A dictionary showing how often each word occurs in this title |
| **Learn** | No file. Applies the selection to your known words instead |

**Yomitan** will export a frequency dictionary, [Yomitan frequency dictionaries](/guides/yomitan-frequency-dictionaries) covers what to do with the file.

## Choosing which words

**Manual** is the default strategy, and **Filter By** decides which slice of the title you take.

- **Full Deck** keeps every word and hides the slider.
- **Top Deck Frequency** orders the words by how often they occur in the text.
- **Top Chronological** order the words in the same way they come first in the text.
- **Top Global Frequency** orders the word by how frequent they are in the global corpus.

If you have selected one of the top options, you'll get access to a slider which will decide which slice of the words you'll keep depending on your selected filter

**Occurrences** takes every word appearing **Over or equal to (≥)** or **Under or equal to (≤)** a **Threshold** in the media.

**Coverage %** works out the fewest words needed to reach a coverage target for the title, and needs you to be logged in. **Start from my current coverage**  counts what you already know towards the target and gives you only the remainder. The target opens at 80%, or at your current coverage of the title when that is already higher, since it cannot be set below where you are.

Untick **Start from my current coverage** and the target is worked out from zero, useful if you want to share it with someone else.

**Then Sort By** orders whatever came out: **Chronological**, **Global Frequency**, **Deck Frequency** or **Random**.

## The options

Some advanced options can be useful:

- **Exclude Kana-only Words** drops words written without kanji.
- **Exclude Example Sentences** removes them from the generated deck. It is unavailable for anime, dramas, movies, manga and audio, which have no example sentences to begin with.
- **Exclude Mature, Mastered & Blacklisted Vocabulary** and **Exclude All Tracked Vocabulary** are the two ways to leave out what you know. The first still includes words you are part-way through studying, the second removes everything you track. Both need an account.
- **Use Custom Dictionaries** adds your own definitions into the Anki and CSV output, and is ticked already if you have [imported any](/guides/custom-yomitan-dictionaries).

**Result: approx N cards** at the bottom updates as you change settings so you can plan a manageable deck.

## Import into Anki

Open the `.apkg` to start importing it into Anki. Each card carries the word, its furigana and reading, English definitions grouped by part of speech, how many times it occurs in the title, and its global rank.

::tip
Keep new cards per day modest. 10 to 20 alongside actual reading is plenty, and finishing a deck will do more for your motivation than starting a bigger one.
::

## The Learn option

**Learn** applies the same selection to your account instead of downloading a file. See [Building your starter vocabulary
](/building-your-vocabulary#titles-you-have-already-finished) for more info.

If you would rather avoid using Anki at all, the built-in [SRS](/guides/using-the-srs) is a great and easy to use alternative.
