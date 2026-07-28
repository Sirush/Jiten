---
title: Make an Anki deck from your own text
summary: Paste Japanese text to look up how it breaks into words, or turn a whole text into an Anki deck.
category: Studying
level: beginner
order: 50
icon: material-symbols-light:post-add
draft: false
updated: 2026-07-28
---

Not everything you read is in the library. Jiten has two public tools for text you bring yourself: one for looking up a sentence, one for turning a whole text into an Anki deck. Neither needs an account.

## Looking up a sentence

Paste Japanese into the search box on the home page. If the text matches dictionary entries you get those first, with a **View parse results for "..."** button underneath; press it to see the text broken into words. When nothing matches the dictionary, the breakdown appears on its own.

Matched words are underlined. Click one for its reading, meanings, frequency rank, pitch accent, kanji breakdown and example sentences, the same panel you get on a full [vocabulary page](/guides/reading-a-word-page).

This one is for a sentence or a short paragraph. Romaji works too, so `neko` finds 猫.

A `*` anywhere in the box turns it into a wildcard search instead. Text with no Japanese in it at all is treated as an English search.

## Turning a text into a deck

For anything longer, go to **Tools** in the header and press **Create Custom Deck**. Paste up to **200,000 characters** into **Your text**, press **Parse Text**, and wait a few seconds.

You get a stats card covering:

- **Character count** and **Word count**
- **Unique words**, and how many of those appear only once
- **Unique kanji**, and how many of those appear only once
- **Average sentence length**
- **Dialogue**, if your text marks speech with 「」 or 『』. It is the share of characters inside those quotes, so a text that punctuates dialogue some other way will not show this row at all.

Then press **Download Deck** for an `.apkg` you can import straight into Anki.

The deck is the complete vocabulary of the text, with the cards in the order the words first appear. There are no filters here: no frequency range, no excluding words you already know, no other file formats. If you want any of that you can create a [word list](/using-the-srs) in the study tab.

## What happens to your text

The custom deck builder holds your text only for as long as it takes to parse it. Nothing is saved, nothing is added to the public library.
