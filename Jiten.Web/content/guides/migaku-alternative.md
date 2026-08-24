---
title: Jiten vs Migaku
seoTitle: "Migaku Alternative: Jiten vs Migaku for Japanese Immersion (2026)"
summary: Jiten as an alternative to Migaku, what each does best, and where they overlap.
category: "Coming from another app?"
level: beginner
order: 20
icon: material-symbols-light:compare-arrows
draft: false
updated: 2026-08-23
published: 2026-07-28
verified: 2026-08-23
---

Migaku and Jiten both help you learn Japanese through immersion, but they are different kinds of product, so "versus" is a little misleading. This page explains how they compare so you can decide which fits your workflow — or whether to use them together.

## The short version

Jiten is a **free, open platform** built around a browsable library of real media: you come here to decide *what* to read or watch next, see exactly how hard it will be for you, and study its vocabulary for free. Migaku is a **paid immersion toolkit**: browser tools and mobile apps that turn content **you already have** (Netflix, YouTube, your own files) into interactive study material, with one-click sentence mining and its own flashcard system.

Jiten offers most of the features of Migaku and more, for absolutely free. The biggest practical difference: **Migaku is easier to set up, Jiten gives you far more control**. The rest of this page unpacks that trade-off.

## Feature comparison

| Feature | Jiten                                                                                                                                                                   | Migaku                                                                                                                       |
|---|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------|
| Price | **Core features free**, optional Jiten+ subscription                                                                                                                    | Paid only with a 10-day free trial, no free tier                                                                             |
| Cost | Optional subscription, 5€/month or 50€/year                                                                                                                             | $10/month Standard, $15/month Early Access, $499 lifetime                                                                    |
| Setup | Website works instantly; the reader and mining setup can take a few minutes                                                                                             | **Very easy**: one extension, guided onboarding                                                                              |
| Customisation | **Deep**: FSRS tuning, deck filters, custom dictionaries, custom meanings, display options                                                                              | Limited, fixed pipeline                                                                                                      |
| Media library | **Browsable library** of thousands of titles with stats and decks                                                                                                       | No library, bring your own content                                                                                          |
| Difficulty | Algorithmic score + community votes + personal coverage                                                                                                                 | Personal comprehension score, relative to your known words                                                                   |
| Known-word tracking | Yes                                                                                                                                                                     | Yes                                                                                                                          |
| Known-word import | **Anki, JPDB, text lists, word sets**                                                                                                                                   | Import from **Anki**                                                                                                             |
| Ready-made frequency decks | **Yes**, one click per title, free                                                                                                                                      | No, you mine cards one at a time as you watch or read                                                                        |
| Read any web page with parsing | **[Jiten Reader](/reader)** browser extension: free & open source, parses any page, word colouring by known status, one-click lookups/SRS/mining synced to your account | Migaku browser extension (paid): parses any page, word colouring by status, one-click lookups and mining synced to your account, AI explanations |
| Sentence mining from video | Yes, with [Jiten Reader](/reader) and asbplayer                                                                                                                         | Yes, one-click cards with screenshot, audio, and sentence from Netflix, YouTube, Disney+, Rakuten Viki, Animelon, and local files |
| Built-in SRS | FSRS-6                                                                                                                                                                  | Migaku Memory (proprietary algorithm)                                                                          |
| SRS control | **Retention target, workload curve, optimisation**                                                                                                                      | Fixed algorithm, no tuning                                                                                                   |
| Custom dictionaries | **Yes** (Yomitan format)                                                                                                                                                | Built-in dictionaries only                                                                                                   |
| Anki support | **Full deck export** with powerful filters                                                                                                                              | Official add-on to send mined cards to Anki                                                                                  |
| Data export | **Everything**: Anki decks, CSV, full account data via the API                                                                                                          | Mined cards to Anki via the add-on                                                                                           |
| Public API | **[Yes](/guides/using-the-api)**                                                                                                                                        | No                                                                                                                           |
| Mobile apps | Website works on mobile                                                                                                                                                 | **Native iOS + Android apps**                                                         |
| Open source / open data | **[Yes](https://github.com/Sirush/Jiten) (CC BY-SA data)**                                                                                                              | Closed platform                                                                          |

This list is not exhaustive; if you see important things to add here or wrong facts, please contact me. Migaku prices as listed on [migaku.com/pricing](https://migaku.com/pricing) as of August 2026.

## Setup and learning curve

**Migaku is easier to start with.** You install one extension, log in, and everything (dictionary, word tracking, mining, SRS) is in that single package with a guided onboarding. Within ten minutes you can be mining cards from Netflix. That premium experience is a real part of what you pay for.

**Jiten is easy to *study* on, but the full immersion workflow can take a bit more time to setup.** Browsing the library, checking difficulty, and studying a deck works instantly with just an account. But to replicate Migaku's all-in-one pipeline, you combine pieces: the [Jiten Reader](/reader) extension for web pages, [asbplayer](https://github.com/killergerbah/asbplayer) for video, [mokuro](https://reader.mokuro.app) for manga. Each piece takes a few minutes to set up.

This trade-off is deliberate: instead of one closed pipeline, you get open tools that each do their job well, work with each other, and keep working even if you stop using Jiten.

## Customisation and control

The extra setup buys you a level of control Migaku doesn't offer:

- **An SRS you can tune.** Jiten uses FSRS-6, the open algorithm also used by modern Anki. You can set your retention target, see the workload-vs-retention curve before changing it, and optimise it so it adapts to your own memory. See [Tuning FSRS](/guides/tuning-fsrs). Migaku Memory is a fixed algorithm with no knobs.
- **Decks built with filters, not one card at a time.** Generate a deck from any title and filter it by frequency, known status, word sets, and more. Pre-mine instead of mining every card manually. See [Generating Anki decks](/guides/generating-anki-decks).
- **Custom dictionaries.** Load your own Yomitan-format dictionaries. See [Custom Yomitan dictionaries](/guides/custom-yomitan-dictionaries).
- **Custom meanings and sentences.** Add custom definitions, mnemonics or example sentences to any word. See [Custom meanings and sentences](/guides/custom-meanings-and-sentences).
- **Display customisation.** Control furigana, word colouring, and how Japanese text is displayed across the site. See [Customising the display](/guides/customising-display).
- **Word sets and imports.** Bring your known words from [Anki](/guides/importing-known-words-from-anki), [JPDB](/guides/import-from-jpdb), or plain word lists, complete the gaps with [word sets](/guides/word-sets).
- **A full API.** Everything on the site is available for developers to build powerful plugins. See [Using the API](/guides/using-the-api).

## Your data stays yours

With a subscription product, it's worth asking: what happens to your reviews if you stop paying?

On Jiten, you can export everything: your learned vocabulary, your reviews, all your data. Because Jiten uses FSRS, an open standard shared with Anki, your scheduling history transfers cleanly rather than starting from zero. If you stop using Jiten one day, you can easily transfer your data somewhere else, without the fear of losing anything.
