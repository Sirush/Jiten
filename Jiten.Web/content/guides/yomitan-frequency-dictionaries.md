---
title: Install Jiten's frequency dictionaries in Yomitan
summary: Add Jiten's global, per-media-type and kanji frequency data to Yomitan so every lookup shows a rank.
category: "Advanced & tools"
level: beginner
order: 40
icon: material-symbols-light:sort
draft: false
updated: 2026-07-28
---

A frequency dictionary shows you how common a word is in Yomitan and other tools that support the format such as JL, so you can easily find common words worth mining. Jiten publishes its own, built from the whole library and updated frequently, free to download and free to redistribute under CC BY-SA 4.0.

## What is available

Open **Tools** in the header and look at the **Frequency Lists** card, where you'll find several lists, each as a **Yomitan** dictionary or a **CSV**:

- **Global**, covering the entire library. Recommended, you can also get it directly from Yomitan with the **Get recommended dictionaries...** button.
- One per media type. Worth having if you immerse mostly in one medium, because a word ranked #20,000 overall can be top 3,000 in visual novels.
- **Kanji**, which ranks individual characters rather than words. Yomitan shows it on kanji lookups.

## Installing

1. Press **Yomitan** on the row you want. You get a `.zip`.
2. In Yomitan, open the settings, go to **Configure installed and enabled dictionaries…**, **Import**, and import the zip without extracting it.
3. Lookups now carry a Jiten rank.

The global list appears in Yomitan as **Jiten**. The others keep their type in the name, so the visual novel list shows as **Jiten (VisualNovel)** and the kanji one as **Jiten (Kanji)**. Installing several at once is fine as they are separate entries.

## Updating them

The lists are rebuilt as the library grows, but Yomitan will not fetch a new version on its own. At least once a month, open Yomitan's settings, go to **Configure installed and enabled dictionaries...**, press **Check for updates**, and click the blue exclamation mark that appears next to any dictionary with a newer version.

## Reading the numbers

**Rank #1 indicates the most common word.** The higher the number, the rarer the word.

The ranking deliberately favours words that turn up across many different titles over words repeated heavily in one series, so a low rank means you are genuinely likely to meet the word again rather than that one author was fond of it.

A kanji word usually shows two numbers. The plain one is the rank of the kanji spelling; the one ending in **㋕** is the rank of the kana spelling. Comparing them tells you which way the word is normally written.

::note
These are the same ranks the site itself uses. The **Rank #** on a [word page](/guides/reading-a-word-page) is that form's entry in the global list.
::

If you want a list narrowed to your own taste, filtered by media type, genre, tag, year or difficulty, [Jiten+](/jiten-plus) can [build one for you](/jiten-plus/frequency-lists). Those come in two kinds: a one-off download, and a saved list which keeps a permanent link and can be auto-updated like the lists above.
