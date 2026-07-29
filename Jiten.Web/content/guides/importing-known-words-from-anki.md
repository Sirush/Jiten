---
title: Importing known words from Anki
summary: Bring your Anki words into Jiten, with their review history, so coverage reflects what you already know
category: "Coming from another app?"
level: beginner
order: 40
icon: material-symbols-light:move-to-inbox-outline
draft: false
updated: 2026-07-28
---

If you have studied in Anki, you do not need to mark thousands of words by hand. Go to **Settings → Vocabulary**, leave **Mode** on **Import**, and pick one of the five method tiles: **AnkiConnect**, **JPDB**, **Anki File**, **Frequency Range** or **Complete Backup**.

## AnkiConnect

This is the recommended mode. It reads your collection directly and brings the review history with it, so your words keep the maturity they had in Anki.

Set it up once:

1. Install the [AnkiConnect add-on](https://ankiweb.net/shared/info/2055492159) in Anki.
2. In Anki, open **Tools → Add-ons → AnkiConnect → Config**, add `"https://jiten.moe"` to the `webCorsOriginList`, then restart Anki.
3. Leave Anki running while you import.

Then, on the **AnkiConnect** tab:

1. Press **Connect to Anki**. Leave **API key (optional)** blank unless you set one in AnkiConnect's config.
2. Choose a deck and press **Next**.
3. Choose the field holding the word **without furigana**. Each option shows a sample value from your deck to help you choose the right one. If possible, pick a reading field as well, which separates words that share a spelling. Full kana and `下[くだ]さる` style furigana both work.
4. Look over the three checkboxes, then press **Import**.

The boxes are **Import review history**, on by default, **Update words you already track**, off, and **Parse words instead of importing them directly**, off. The last one is for decks whose words are conjugated rather than in dictionary form; it runs everything through Jiten's parser, which handles the conjugation at some cost in accuracy.

 Cards you have never studied in Anki are not imported. Words Jiten cannot find in its dictionary are skipped and listed for you afterwards. Words you already track are skipped too, unless you tick **Update words you already track**.

::note
**Update words you already track** brings Anki's review history across for words you already track and merges it into what Jiten has recorded, rather than replacing it. Reviews already stored for the same moment are not added twice, so importing the same deck again does not inflate anything. Scheduling only moves to Anki's version when Anki reviewed the word more recently than you did in Jiten, so whichever side you have been studying decides when the word comes back.
::

This is the box to tick when you want to keep Jiten up to date with Anki. Run the import again after an Anki session and your reviews carry across; there is no need to delete anything first.

## Anki File

For a plain list, export from Anki with **Export format: Notes in Plain Text (.txt)** and every box unticked, then upload it on the **Anki File** tile. A `.csv`, or a file you wrote yourself with one word per line, works the same way. Jiten takes everything before the first tab, or failing that the first comma, and skips lines starting with `#`.

Every word in the file becomes **Mastered**, which means it counts towards coverage and is never scheduled for review.

::warning
This overwrites words you already track. Anything blacklisted or part-way through study becomes **Mastered**. Remove the lines you do not want before uploading, and take a **Complete Backup** first if you have set states by hand.
::

## Frequency Range

With no history to import, the **Frequency Range** tile marks a band of the most common words as **Mastered**. Set the top of the range to roughly where you think your vocabulary reaches.

## Checking your results

The **Vocabulary Management** card at the top of the page re-counts as soon as any import finishes, splitting your words into young, mature, mastered and blacklisted.

Coverage across the library is recalculated separately in the background, which can take a few minutes. **Settings → Coverage** shows when the refreshing is finished.

## Backups

**Mode → Export** offers a **Word List** as a text file split by state, and a **Complete Backup** as JSON carrying card states, review history, stability, difficulty and due dates. It is recommended to export backups regularly, especially before taking any actions that will mass import words.

See [Tracking what you know](/guides/tracking-known-words) for what the word states mean once your words are in, and [Migrate from JPDB to Jiten](/guides/import-from-jpdb) if you are coming from there instead.
