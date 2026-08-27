---
title: Using custom Yomitan dictionaries
summary: Import your own Yomitan dictionaries so their definitions appear across the Jiten website and in your exported decks.
category: 'Advanced & tools'
level: advanced
order: 30
icon: material-symbols-light:menu-book-outline
draft: false
updated: 2026-07-28
---

If you have a monolingual dictionary, a specialised glossary, or anything else in Yomitan format, you can load it into Jiten. Its definitions then show up wherever words appear on the site, and in the Anki and CSV decks you download. They do not appear in Jiten Reader or any external tool.

## Importing

Go to **Settings**, find the **Advanced** section, and open **Dictionaries**. Press **Select File** and pick a `.zip`, or drop one onto the import area if you are on a desktop. Leave the zip as it is; Jiten reads it without unpacking.

Wait a few seconds while it reads the file, parse the term banks and store the entries and you'll get a confirmation with the entry count. A large dictionary takes a while, and it all happens in the page, so leave the tab open.

::note
Your dictionaries live in your browser and are never uploaded to Jiten's server. That keeps them private, and it also means they are per device: another browser or computer needs its own import, and clearing your browsing data removes them.
::

Only the definitions are read. Frequency, pitch accent and kanji data in the same zip is ignored, so a dictionary that contains nothing but frequency data will be refused. Images in definitions are removed, and where a dictionary has several entries for a word, only the first is used.

## Organising your dictionaries

Each row has a mode:

- **Always show**: its definitions are used whenever it has a match.
- **Fallback**: used only when no dictionary above matched the word.
- **Disabled**: never used, without deleting it.

::tip
The list works top to bottom. If you want to avoid English definitions as much as possible, then put the built-in dictionary last as a **Fallback**, in case your other dictionaries don't have an entry for the word.
::

## Seeing the definitions

The definitions from your imported dictionaries show up on word pages, in vocabulary lists, on your SRS cards, and in the expanded results on the search page. When several dictionaries are available for a word, you will be able to select which dictionary you want to see the definition from with tabs. Your last chosen tab will be remembered.

Your custom dictionaries definitions can also be included in downloads. Tick **Use Custom Dictionaries** in the download dialog for **Anki** or **CSV**, and your definitions are added to the downloaded decks.

::warning
That checkbox is ticked by default as soon as you have imported one dictionary, so every deck you download from then on uses your definitions. Untick it if you want a plain JMDict deck.
::

Words with no match in your dictionaries keep their JMDict definition in the file, so a sparse dictionary gives you a mixed deck rather than a broken one.

## Managing your dictionaries

You can rename then at any moment with the pencil icon, or remove them with the bin one.

There is no limit on how many you can add beyond your browser's own storage.
