---
title: Tuning FSRS
summary: 'Set a retention target, fit the scheduler to your own history, and lighten the days you choose'
category: 'Advanced & tools'
level: advanced
order: 10
icon: material-symbols-light:tune
draft: false
updated: 2026-07-28
---

FSRS works well on its defaults, so none of this is needed to get started. Once you have a few months of reviews, the controls in **Settings → Study (SRS)** can cut your daily workload or push your recall higher.

That page has two cards. **SRS Study** comes first and holds load balancing, easy days and your timezone. **FSRS Settings** is below it and holds everything else here: the retention target, the simulation, the optimiser, the health check, the raw parameters and the reschedule button. You can check [Understanding your SRS stats](/guides/understanding-your-stats) for more detailed explanations.

## Desired retention

The recall rate FSRS aims for, and the main dial on your workload. It defaults at `0.9`, which means a retention target of 90%. Push it higher means you should forget words less at the cost of a lot more reviews, growing almost exponentially at higher retention rate. Lower means noticeably fewer reviews, which can help avoid burn out and spending less time, at the cost of forgetting a bit more words.

Saving a new value does not touch your existing cards until you do it explicitly. It applies to each card the next time you grade it, or to everything at once if you run **Reschedule all cards**. That is the usual reason a change appears to have done nothing.

## Workload simulation

Rather than guessing the trade-off, press **Simulate**. Jiten projects a year forward from your actual cards and your measured review speed, and reports reviews per day, minutes per day and the share of your collection you would recall. Drag the slider afterwards to see how a different retention rate would affect your workload.

**Include future new cards in the estimate** show the estimates if you continue adding new cards daily as your rate set in **New cards per day**, which is more accurate if you plan to continue studying at the same rate.

## Optimise parameters

The defaults describe an average learner. **Optimise** fits them to your own review history.

It is recommended to wait to have a few hundreds reviews to start optimising, then to optimise monthly or whenever your reviews double.

**Also reschedule all my cards after optimisation**, on by default, will change the due date of all your cards immediately. Untick it if you would rather see the new parameters first and reschedule later.

## Review history health

Between the description and the **Optimise** button, a bar shows how often you press each button, with your total review count and what share of your reviews repeat a card the same day. Four things can be flagged:

- Plenty of **Hard** and almost no **Again**, which means Hard is doing duty as a fail button. In FSRS, Hard means you recalled it with effort, so grading a genuine failure as Hard teaches the optimiser that you succeeded. This is the most common issue which leads to cards being pushed far back.
- Never having pressed **Hard**, in your whole history.
- Never having pressed **Easy**, likewise.
- 40% or more of your reviews repeating a card the same day, which makes your history a poor guide to long-term memory.

That first message comes with a **Remap Hard → Again** button, which rewrites every past **Hard** to **Again** and reschedules. It cannot be undone, so use it only if that really is what you were doing. It reschedules using your current parameters, which were trained on the data it has just corrected, so run **Optimise** again afterwards.

## Load balancing and easy days

Both of these are in the **SRS Study** card, under **Review scheduling**.

Reviews are load-balanced automatically. Each card lands on the least busy day inside the window FSRS was already going to randomise it into, so a heavy import does not become a single loaded day. The window is only as wide as the interval allows: roughly two days either side at a ten-day interval, three at a month, a week at a hundred days. Cards on intervals under three days are not moved at all.

**Easy days** goes a step further and lets you name the days you want to be lighter, for the ones you know you will be busy with other activities. Turn it on and the options appear. **Lighter weekends** covers the common case: set a **Weekend load** of **Reduced** or **Minimum** and Saturday and Sunday are pushed down accordingly. **Custom per day** gives you the same choice for each day of the week individually, so a busy Wednesday can be set to **Minimum** while the weekend stays **Normal**. Which day is which is decided by the timezone under **Day boundary**, right above.

Balancing is best effort: cards will be moved within their window, so a day you marked **Minimum** is avoided when possible rather than kept completely empty. Like the retention target, changing easy days leaves existing due dates alone until each card is next reviewed or you reschedule everything.

## The raw parameters

**Advanced: edit raw parameters** opens the 21 numbers behind the scheduler. **Show parameter breakdown**, inside it, adds a table naming each one and giving its default. **Optimise** writes these for you; editing them by hand is for people who know exactly which value they want to change.

**Reset to default** puts both the parameters and the retention target to the default.

## Rescheduling

**Reschedule all cards** recomputes due dates from your current parameters by replaying your review history. It is the way to make a new retention target or a new set of easy days apply to cards you already have. Cards you have never reviewed are left alone, since there is nothing to replay.

It is a pure replay, so it is safe to repeat and gives the same answer every time. It can also drop a large number of reviews on you at once, which is what the warning above the button is about.
