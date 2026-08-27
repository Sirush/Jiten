---
title: Migrate from JPDB to Jiten
seoTitle: 'Export JPDB to Anki or Jiten: Migration Guide'
summary: Bring your JPDB words and review history across so coverage and decks reflect your real level from day one.
category: 'Coming from another app?'
level: beginner
order: 30
icon: material-symbols-light:move-item
draft: false
updated: 2026-08-23
published: 2026-07-28
verified: 2026-07-28
---

Switching from JPDB only takes a few minutes. Your known words are imported, and your review history can be too, so Jiten starts pretty much where you've left off.

If you are still weighing the two, [Jiten vs JPDB](/guides/jpdb-alternative) covers the differences.

::warning
The review history import overwrites scheduling on every word it touches, and there is no undo. If you have already been studying in Jiten, take a backup first: **Settings → Vocabulary**, switch **Mode** to **Export**, and download a **Complete Backup**. Restoring it later means ticking **Overwrite existing cards**, which is off by default, and it will not remove cards the import created.
::

## What you need from JPDB

- **Your API key**, at the bottom of [JPDB's settings page](https://jpdb.io/settings). This lets Jiten read your word lists.
- **Your reviews export** (`reviews.json`), also from JPDB's settings, if you want your review history as well.

It is recommended to use both together, as the API key doesn't expose the review history, and the reviews.json doesn't contain your special decks.

## Importing into Jiten

Go to **Settings → Vocabulary**, leave **Mode** on **Import**, and pick **JPDB**.

Paste your key into **JPDB API Key**. The key is used from your own browser to talk to JPDB directly, is never sent to Jiten and is never stored.

It's recommended to leave **Import additional readings within frequency range of the imported reading (only the most frequent reading by default)** ticked. Most words can be written more than one way, and this marks the alternative spellings of your known words as known too. **Frequency range:** controls how far it reaches, counted from each word's most common form, so the default of 15000 covers reasonably common alternatives without dragging in obscure ones.

To bring your history, press **Choose reviews.json** and pick the file. Then press **Import from JPDB**.

## Overwriting existing cards

Above the button sits **Overwrite existing card states (mastered, blacklisted, suspended) with review history**, ticked by default. It decides whether your review history is allowed to replace a **Mastered**, **Blacklisted** or **Suspended** state that a card already has.

**Coming straight from JPDB with an empty Jiten account**, you can simply put the key and the file in together and leave the default settings on.

**If you have already been studying in Jiten**, be careful to untick the overwrite box unless you want to overwrite your existing states.

::warning
Always do a full export of your Jiten data before doing any import action.
::

## What is imported

With the API key, your JPDB states map over directly: **known** and **never-forget** become **Mastered**, blacklisted stays **Blacklisted**, and suspended stays **Suspended**.

Words that come in through `reviews.json` are scheduled from their history instead, so they arrive as ordinary review cards with a due date rather than as **Mastered**.

Jiten reads all of your JPDB decks, plus your never-forget and blacklist decks.

Two things worth knowing:

- **Data exposed through the API key is limited**: only known, never-forget, blacklisted and suspended cards are discoverable. Anything else, including words you are part-way through, are skipped. The reviews file is the only way to bring those in.
- **Words you already track in Jiten are left alone by the key import**, for the spelling you track them under. If a word has other spellings that the additional-readings option picks up, those still arrive as new **Mastered** entries.

Deck names, JPDB's card levels, kanji cards, example sentences, JPDB's media, etc, are not imported.

::note
JPDB's own scheduling does not transfer. Jiten replays your review history through FSRS from scratch, using your own FSRS settings, so intervals will not match what JPDB showed. Your progress is preserved but the dates might be different depending on the settings you used on JPDB.
::

## Afterwards

Both imports queue a coverage recalculation across the whole library which can take a few minutes. Check **Settings → Coverage** to see when the date is refreshed.

If your coverage number still look off after import, try following the steps in [Building your starter vocabulary](/guides/building-your-vocabulary).

From there, you can study with the [built-in SRS](/guides/using-the-srs), or [generate Anki decks](/guides/generating-anki-decks) that filter out everything you just imported.

## From JPDB to Anki

JPDB has no direct Anki export, but Jiten can work as the bridge: import your JPDB words and history as described above, then [generate Anki decks](/guides/generating-anki-decks) from any media or frequency list with your known words filtered out. You can also export your full vocabulary as CSV from **Settings → Vocabulary**.
