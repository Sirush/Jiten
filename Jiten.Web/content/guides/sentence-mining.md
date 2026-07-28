---
title: Sentence mining with Jiten
summary: Capture a word together with the sentence you met it in, and study it in the SRS without leaving what you were reading.
category: Studying
level: beginner
order: 70
icon: material-symbols-light:diamond-outline
draft: false
updated: 2026-07-28
---

Sentence mining means capturing a word together with the line you found it in, in order to study it in context. The word will come with your memories of the scene, which is why mined words stick better than pre-made lists. With [Jiten Reader](/guides/browser-extension) it is one button.

## Setting up, once

First, you need somewhere for the words to go. On the [study decks page](/srs/decks) press **Add Deck**, choose **Word List**, give it a name like `Mining`, and press **Create Word List**. This is the only deck type that accepts individual words; media and frequency decks cannot.

Two important things to note:

- It has to be active so the new cards come in your study sessions.
- With the default **Top deck** setting for new card gathering, all your new cards come from the highest deck in the list as long as it has words you haven't seen yet. If you have a big media deck above your mining deck, move the mining deck to the top with the up arrow, or switch **New card gathering** to **All decks equally** in your SRS settings.

Then point the extension at it. In Jiten Reader's settings, open **Mining** and set **Target word list** to your new deck.

If you leave **Auto-mine to selected word list** off, then you'll get a deck picker each time, listing only your word lists, which is useful if you sort words into several decks. Turn it on to send everything straight to the target deck.

## While you are reading

Hover a word you want, press **Shift** to open the popup, then press **Deck +**.

That is it. The word is added to your list and the surrounding sentence is saved as a custom example sentence, so when the card comes up for review it shows the line you found it in.

The button tells you where you stand. **✓ In list** means the word is already in one of your word lists. With auto-mine on it goes further and shows **In deck**, greyed out, when the word is already in the deck you are mining to.

::note
Mining keeps up to 3 sentences per word, or up to 10 with Jiten+. If a word ends up with several sentences, its card shows one of them at random every time the card shows up.
::

You can bind a key to mine quickly under **Keybinds → Review**. **Add to word list** only works when **Auto-mine to selected word list** is on, since there is no picker to open otherwise.

## Choosing what to mine

It's often recommended to mine **i+1**: a sentence where exactly one word is new to you. You understand everything else, so the meaning is inferable from context and the word will be easier to remember. Jiten Reader can mark those for you with **Mark I+1 sentences in the text** under **Text Highlighting**, which is off by default. Tick **Only mark when below frequency rank** underneath it and a **Maximum frequency rank** box appears, so only reasonably common unknowns get marked.

The frequency rank can also be a good indicator. A word you will meet again is nearly always worth a card. Something rare is worth mining only if you want to remember it. Words you understood without help usually do not need a card at all, so [mark them known](/guides/tracking-known-words) instead.

::tip
While mining everything can sound enticing at first, it's a quick way to burn out as you'll see them pile up in your list of new words or reviews, and by the time you get to them you might already know them from seeing them so much. Try different mining strategies and see what works better for you.
::

## Without the extension

You can add any word to a word list from the site. Press the **...** button next to a word anywhere it appears, and pick a list under **Add to deck**. This adds the word only; there is no sentence attached, though you can [write one yourself](/guides/custom-meanings-and-sentences) afterwards.

## Limits and clean-up

Across all your word lists you can hold 150,000 unique words, or 300,000 with [Jiten+](/jiten-plus). There is no per-deck limit. Separately, you can have 60 study decks in total, or 200 with Jiten+, and that count includes your media and frequency decks.

Removing a word from a list leaves its card and its sentences alone. Deleting the whole deck keeps the cards you have already studied and your custom sentences, while the words that never became cards are gone with the list.

::warning
If you have set **Review cards from** to **Study decks only**, deleting a deck also stops its cards coming up for review, because that setting limits reviews to words in your current decks. On the default **All tracked** they keep appearing.
::

::note
Mining pairs well with the pre-made decks rather than replacing them. [Pre-learn a title's most common words](/guides/generating-anki-decks) before you start it to decrease your lookups, then mine the ones you think are the most important or need more context as you go.
::
