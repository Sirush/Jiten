---
title: Tracking what you know
summary: How to mark and import known words so coverage across the site reflects what you actually know.
category: Using Jiten
level: beginner
order: 30
icon: material-symbols-light:checklist
draft: false
updated: 2026-07-28
---

Coverage is only as good as what Jiten knows about you. This page covers every way to tell it what you know: marking words one at a time, importing a history from another app, and applying a whole title in one go.

All the features listed below require a Jiten account.

## Marking words as known

You can click the green **+** next to any untracked work and mark it as **Master** or **Blacklist**.

For a long cleanup pass, open **Display Settings** from the gear in the header and tick **Master in 1 click**. A plain click then masters straight away and the popover stops appearing, while **Ctrl+Click** still blacklists. The setting lives in your browser rather than your account, so it will not follow you to another device.

## Forgetting a word

**Mastered**, **Blacklisted**, **Young** and **Mature** words show a red **−**. That one is **Forget Word**, and it deletes the card together with its review history. It is the right button for something you marked by accident, and the wrong one for a word you have been studying.

To drop a **Mastered** or **Blacklisted** state without losing anything, open the **...** menu next to the word and pick **Unmaster** or **Unblacklist**. The card keeps its history and comes back due straight away.

The rest of that menu depends on the state the word is in, and covers **Suspend**, **Resume**, **Reset schedule** and **Forget**, along with the decks the word belongs to and a link to its **Review history**.

## What the states mean

- **Mastered**: you tell Jiten you know it and will never forget it. No reviews are scheduled.
- **Young**: SRS state, with less than 21 days between the last review and the next one.
- **Mature**: SRS state, with 21 days or more between reviews.
- **Blacklisted**: does two things at once. Jiten never schedules it in your reviews, and it still counts towards your coverage. Use it for names, onomatopeia, and anything you don't ever want to study but still want to be counted in your coverage.
- **Redundant**: covered through another form of the same word, usually the kanji spelling of a word you know in kana. Hover it to see how that other form is tracked.
- **Suspended**: paused. It keeps its scheduling and stops coming up for review until you press the green play button.

**Mastered**, **Mature** and **Blacklisted** words count as known when Jiten works out your coverage, along with the words your word sets cover. Young words are counted separately.

## Importing in bulk

If you have studied elsewhere, import instead of clicking through thousands of words. Go to **Settings → Vocabulary**, leave **Mode** on **Import**, and pick a method.

**AnkiConnect** & **Anki File**: See [Importing known words from Anki](/guides/importing-known-words-from-anki).

**JPDB**: See [Migrate from JPDB to Jiten](/guides/import-from-jpdb).

**Frequency Range** needs no history at all. Give it a range of frequency ranks, up to a maximum of 10,000, and every word in that band is marked as known. It is a rough approximation, and the more your immersion has leaned into particular genres the less global frequency will match your own vocabulary.

**Complete Backup** restores a JSON file exported from Jiten, including card states, review history, stability, difficulty and due dates. **Overwrite existing cards** is off by default, so a restore adds what is missing and leaves the rest alone.

Whichever route you take, the **Vocabulary Management** card at the top of the page tells you how many words you are tracking and how they are split between young, mature, mastered and blacklisted. It updates as soon as an import finishes, so it is the quickest way to check the results.

Export lives on the same page. Switch **Mode** to **Export** for a **Complete Backup**, or a **Word List** as a text file split by state. Take a backup before anything on this page that cannot be undone.

## Word sets

Word sets handle whole categories at once: names, places, particles, and similar groups you do not want to work through individually. They have their own tile at **Settings → Word Sets**. Each set can be set to **Blacklist** or **Mark as Mastered**.

A word set never creates a card, so subscribing to one will not overwrite a state you set by hand. See [Word sets](/guides/word-sets).

## Titles you have already finished

The best source of your real knowledge are works that you have already finished. Open any media, press **Download / Learn**, and pick the **Learn** format, which applies the selection to your account instead of downloading a file. Choose the **Occurrences** strategy, set a **Threshold**, leave **Vocabulary State** on **Mastered (never forget)**, and press **Apply to Vocabulary**.

The threshold is your judgement call to make about that specific title. The default of 10 is a safe setting for something you found hard. Somewhere around 3 suits a title you found comfortable, since a word you met three times in a work you followed easily is probably one you know. Going lower catches more of what you picked up at the cost of erroneously marking some words you did not.

::warning
**Learn** overwrites words you are part-way through studying, turning them **Mastered**. Selecting it ticks **Exclude Mature, Mastered & Blacklisted Vocabulary**, which protects those three states only. Tick **Exclude All Tracked Vocabulary** as well to leave everything you already track alone.
::

Repeat for a handful of finished titles from different genres. Returns drop off sharply within one series: the first volume may add thousands of words and the fifth almost none.

Afterwards, open the **Coverage** tile in your settings and press **Refresh now**. **Learn** does not trigger a recalculation by itself.

## Filling the gaps from words you know

**Mark Words as Known via Composition** sits further down the vocabulary settings page. It works in both directions: it can list the parts of compounds you know, so knowing 突っ込む suggests 突く and 込む, or list compounds whose parts you already know, so 取り and 付ける suggest 取り付ける.

Press **Preview** to see the candidates sorted by frequency, then master or blacklist them one at a time or all at once. Only new words are shown by default. Tick **Learning** or **Mature** to include words you are already studying, keeping in mind that mastering one of those resets its review schedule.

## Mass actions and starting over

**Mass Actions**, on the same page, applies one operation to every card matching a filter: a state change, a due-date shift, a schedule reset, or deleting the cards outright. **Danger Zone**, at the bottom, has **Clear All Known Words**, which deletes every card and all their review history.

::warning
Mass actions and **Clear All Known Words** are permanent and cannot be undone. Export a **Complete Backup** first.
::

For finer work, **Settings → Cards** lets you filter your cards by state and edit them one by one or in a selection.

## Where to go next

- [Choosing media at your level](/guides/choosing-media-at-your-level): helping you pick the perfect media for your level.
- [Importing known words from Anki](/guides/importing-known-words-from-anki): the AnkiConnect route in full.
- [Word sets](/guides/word-sets): kickstarting your coverage.

Having troubles finding the right option? You can always ask on [Discord](https://discord.gg/cZWM7b4wzk) to find some help!
