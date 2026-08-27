---
title: Using the API
summary: Create a key, call the public endpoints, and stay inside the rate limits
category: 'Advanced & tools'
level: advanced
order: 20
icon: material-symbols-light:api
draft: false
updated: 2026-07-28
---

Jiten's API is public. Almost all actions that you can do on the website can also be done programmatically: searching the media library, a title's vocabulary, dictionary search, kanji details, frequency lists. Personal projects, hobby tools and small research scripts are welcome, within the rules in [Can I use the API or scrape the site?](/guides/api-and-scraping).

## The reference docs

The interactive reference is at the root of the API host, [api.jiten.moe](https://api.jiten.moe). Every path below is relative to `https://api.jiten.moe`.

## Most used endpoints

All of these are `GET` and all of them answer without a key.

- `/api/media-deck/get-media-decks` browses the library with the same filters as the site. Every parameter is optional and pages are fixed at 50 results.
- `/api/media-deck/{id}/detail` returns one title's stats. Its subdecks come back 25 at a time.
- `/api/media-deck/{id}/vocabulary` returns its word list. `limit` defaults to 100 and caps at 200, and `sortBy` takes `globalFreq`, `deckFreq` or `chrono`.
- `/api/frequency-list/download` returns the global or per-media-type frequency list. `downloadType` takes `yomitan`, which is the default, or `csv`. See [Yomitan frequency dictionaries](/guides/yomitan-frequency-dictionaries).
- `/api/kanji/{character}` returns readings, meanings, stroke count, JLPT level and the 20 most common words using it.

`/detail` and `/vocabulary` answer an unknown deck id with a 200 and an empty payload rather than a 404, so check the body and not the status on those two.

```bash
curl -G "https://api.jiten.moe/api/vocabulary/parse" \
  --data-urlencode "text=猫が好き"
```

## Getting a key

Anything tied to your account needs authentication: your known words, your SRS, the reader endpoints. An API key covers those.

::warning
The API key gives complete access to your account. Keep it private and only enter it in applications you trust. You can click the regenerate button at any time to deactivate your old key.
::

Generate a key from your [settings page](/settings), in the **Advanced** section, on the **API Key** tile. Press **Generate API key**, confirm, and copy it out of the panel that appears.

- The key is shown **once**. Reload the page and it is gone for good.
- You get **one** key at a time. **Regenerate** revokes the current one immediately, so anything using it breaks the moment you press it.
- The key carries every permission your account has, including the calls that rewrite your known words and delete cards. Treat it like your password.

## Using it as a developer

Send it as a header:

```bash
curl "https://api.jiten.moe/api/media-deck/12345/detail" \
  -H "X-Api-Key: ak_your_key_here"
```

`Authorization: ApiKey ak_...` works as well. `Authorization: Bearer ak_...` does not.

That call also works without the key, and returns the same title with the coverage figures missing. A wrong or revoked key behaves the same way: on a public endpoint it is not an error, you are treated as anonymous. The mistake only surfaces as a 401 on an endpoint that requires an account.

## Rate limits

**300 requests per minute** for ordinary endpoints, and **10 per minute** or less for the heavy ones: deck downloads, frequency lists, the custom deck parser, and some others.

The numbers are the same whether you send a key or not. What changes is the bucket. An authenticated caller gets their own, an anonymous one shares with everyone else on the same IP address.

Going over returns a **429** with a `Retry-After` header and this body:

```
Too many requests. Please try again later.
```

That is plain text rather than JSON, so a client that parses every response will throw on it instead of seeing the status. Small overshoots may not fail at all: a few requests are held and served when the window rolls over, which looks like a call hanging for up to a minute.

## What to expect from it

While the endpoints are generally stable, they are not versioned and can change at any moment without notice.

What you can do with the data you pull is covered by [What can I use the data for?](/guides/deck-licensing).

If you are unsure whether your use case is fine, ask on [Discord](https://discord.gg/cZWM7b4wzk).
