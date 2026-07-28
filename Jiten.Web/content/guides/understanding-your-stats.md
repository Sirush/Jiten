---
title: Understanding your SRS stats
summary: What each chart on the stats page measures, and when a number is telling you to change something
category: Studying
level: beginner
order: 60
icon: material-symbols-light:monitoring
draft: false
updated: 2026-07-28
---

The **Stats** tab of the SRS section is a page filled of interesting charts. This will walk you through them and point at [Tuning FSRS](/guides/tuning-fsrs) wherever a number implies a setting to change.

## Today

Four figures: **Gradings**, **Pass rate**, **Minutes** and **New cards**. They count everything you did today, including learning steps and cards you saw more than once, so the pass rate here is not the same measure as the one in the retention panel below.

## Card states

This simply list out the states belonging to every of your cards.

## Leeches

Cards you keep failing, counted as **Active**, **Suspended** and, once one has recovered to a memory lasting 21 days or more (mature), **Recovered**. Chips below show the ten worst still causing trouble, and **View all** opens them under **My Cards**.

The threshold is 8 lapses by default and is set in your SRS settings. Setting it to 0 removes this panel.

## Retention

The one worth watching. It counts only the first review of a card on any day, and only when a day or more has passed since you last saw it, so learning steps and same-day repeats stay out of it. Anything other than **Again** counts as a pass, including **Hard**.

Three tiles, **Overall**, **Young** and **Mature**, over a window you choose. A badge next to the heading shows you how on target you are regarding your desired retention.

If you are well below target, your parameters may not fit you well. If you are comfortably above it, you are reviewing more than you need to and could lower the target. Both live in [Tuning FSRS](/guides/tuning-fsrs).

## Card Difficulty, Card Retrievability and Card Stability

Three histograms.

- **Card Difficulty** is how hard each card is for you, shown as a percentage rather than the 1 to 10 scale Anki users may expect. A pile-up at the high end is usually leeches worth suspending or [giving a mnemonic](/guides/custom-meanings-and-sentences).
- **Card Retrievability** is the chance you could recall each card right now. It covers cards in review and relearning only, so learning, suspended, mastered and blacklisted cards are absent.
- **Card Stability** is how long each memory currently lasts, bucketed from `<1d` to `1y+`, with a median in days underneath. A median that grows month over month means the words are sticking.

Under the retrievability chart, **Estimated knowledge** adds up those recall probabilities into a single word count, with mastered words reported separately beside it. It measures what you are actively holding in review, so it is narrower than the total below.

## Answer buttons

How often you press **Again**, **Hard**, **Good** and **Easy**, split into **Learning**, **Young** and **Mature**.

Plenty of **Hard** with almost no **Again** means you are using Hard as a fail button, and the health check in [Tuning FSRS](/guides/tuning-fsrs) can detect and repair that.

## Hourly breakdown and Review time

**Hourly breakdown** plots reviews per hour of your local day with a pass-rate line over the top. **Review time** buckets how long each card takes, with a running total in hours and an average that splits into 30-day and all-time figures once both windows have reviews. The average caps each card at 120 seconds so one interrupted review does not skew it; the total does not. This can be a good way to find at which time of the day you're the most efficient and adapt your habits.

## Words learned over time

This plot **Mature** and **Mature + young** over weeks, or months once you have more than about a year of history. Because it is a real history it can dip when cards lapse or get reset.

**Save as image** exports the chart so you can share it easily with others.

## Forecast

Reviews due per day over **30d**, **90d** (the default) or **365d**, with a cumulative line. It plots the due dates your cards already carry, which makes it the place to look after an import that brought scheduling with it.

Nothing here is projected forward, so cards you have not studied yet are absent and raising your daily new-card limit changes nothing on this chart until those cards have actually been seen. Everything overdue is folded into today's bar, mastered, suspended and blacklisted cards are left out, and if your **Review cards from** setting is on **Study decks only** the forecast follows that too.

::warning
Most of these stats are unavailable until you have enough data.
::
