# Changelog

All notable changes to this project are documented here. Versions use Jellyfin's four-part format.

## 1.1.2.0

- Posters for existing collections: one button finds every collection without a poster and gives each one a TMDB poster or a collage. Collections that already have a poster are left alone, and locked collections skip TMDB.

## 1.1.1.1

- New collection poster: when Apply creates a collection, it is looked up on TMDB (only an exact name match, with or without "Collection"). If there's no match, a 2×2 collage is made from the first four posters in your list. Choose "Find on TMDB only" or "Off" instead under **New collection poster**. The report says which poster was used.
- "Lock metadata" is now applied after the poster is added, so it no longer blocks the artwork.
- Renamed the product to **Paste Collections** (same plugin GUID). Dashboard page and API routes are now under `PasteCollections`.
- GitHub repository renamed to [PasteCollections](https://github.com/xRaid3nx/PasteCollections) (old `JellyfinCollectionsPlugin` URL redirects).

## 1.1.1.0

- Not found rows show only the Seerr button or status badge, without the "—" placeholder or the repeated message. The reason is still shown when a request fails.

## 1.1.0.0

- Seerr requests: titles that are Not found can be requested in Seerr (or Jellyseerr/Overseerr) from the preview or the report, one at a time or all at once.
- New Seerr panel for the server address and API key, with a connection test.
- A title is requested only when Seerr has one clear match (same title, and same year if given); otherwise you pick from a dropdown. Titles Seerr already has requested or available are skipped.

## 1.0.1.0

- New two-column layout: the list on the left, settings and Preview on the right. It stacks into one column on narrow screens.
- The Titles box now fills its column, and its label sits above it.
- Preset rows and the results toolbar are lined up with their buttons.
- Fix: applying or previewing again within a second or so of an Apply now correctly sees the items that were just added (and never creates a second collection with the same name).

## 1.0.0.0

First release, for Jellyfin 12.2.

- Dashboard page: paste a list, preview matches, and apply the selected items to a new or existing collection.
- Statuses: Matched, Already in collection, Choose one, Year off by 1, Close spelling, Different year, Not found.
- List parser that handles years in several formats, bullets, numbering, quotes, comments, BOM, and CRLF/LF, and reports duplicate lines.
- Title normalisation: accents, punctuation, articles, "Title, The", `&`/and, roman numerals and number words, and studio prefixes.
- Fuzzy suggestions (90 %+ similar) that are never selected automatically.
- Presets and remembered options, stored in the plugin configuration.
- Report with "Copy Not found" and "Copy full report".
- Admin-only REST API under `/BatchCollections`.
