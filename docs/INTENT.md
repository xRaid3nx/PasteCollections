# Intent

**Paste Collections** is a server-side Jellyfin plugin (`Jellyfin.Plugin.Collections`) for Jellyfin 12.2 / .NET 10.

## Goal

On a dashboard page, paste a list of titles (one per line, optional year), type a collection name, and pick Movies / Shows / Both. **Preview** matches every line against the library and changes nothing. **Apply** adds exactly the confirmed matches to a collection with that name. If the collection doesn't exist it's created. Titles that aren't in the library are skipped and reported.

Why: hundreds of Disney movies shouldn't have to be added to collections one at a time.

Secondary goal: publishable later (clean, documented, GPL-3.0, tested, installable from a plugin repository manifest). Until then GitHub is a private backup only.

## Behaviour (summary of build prompt §4)

- Inputs: list (max 5,000 lines / 1 MB), collection name (with existing-name suggestions), media type (Movies / Shows / Both; shows are Series only), year tolerance (exact / ±1), fuzzy on/off, lock metadata (new collections only), presets saved in plugin config, last-used options remembered (the collection name is not; it starts empty on every visit so a list isn't added to the wrong collection by accident).
- Parser: skips blank lines and `#` / `//` comments. Strips bullets, numbering, quotes, and trailing `,` / `;`. Years are read from `(1994)`, `[1994]`, `- 1994`, `, 1994`, `| 1994`, or a TAB before the year. A bare trailing number is treated as part of the title first, then retried as a year. Duplicate lines are collapsed.
- Normalisation: strict key (diacritics, case, punctuation, articles, trailing year tag) and loose key (strict, plus studio prefixes and number words / roman numerals up to 10).
- Statuses: Matched, AlreadyInCollection, Ambiguous, PossibleYearOffByOne, PossibleFuzzy (≥ 0.90), YearMismatch, NotFound. Fuzzy and off-by-one matches are never auto-selected.
- Collections: case-insensitive exact name lookup. If several have the same name, use the largest and warn. Create or add in one call, filter out existing members, never remove anything. Items are looked up server-wide.
- Report: counts plus a table, with Copy buttons for "Not found" and for the full report.
- Admin-only REST API under `/PasteCollections`. Uses only official `ICollectionManager` / `ILibraryManager` APIs, never direct DB access, never legacy `/emby` URLs.

## Edge cases that must be handled and tested (§8)

- Same title in different years (Lion King, Aladdin, Beauty and the Beast, Cinderella, Dumbo, Jungle Book, Mulan, Lilo & Stitch), with and without a year in the list.
- Name variants: `Lion King, The`, `Frozen II` / `Frozen 2`, `WALL·E` / `WALL-E`, `Wreck-It Ralph`, `Tron: Legacy`, `Disney's A Christmas Carol`, colon variants, `Spider-Man: Into the Spider-Verse`. Known limit: `101 Dalmatians` vs `One Hundred and One Dalmatians`.
- Titles that are numbers: `2012`, `1917`, `Blade Runner 2049`, `300`.
- A movie and a series with the same name. Items with no year.
- Diacritics, CRLF / LF, a UTF-8 BOM, tabs, trailing spaces, pasted bullets.
- Idempotent re-runs, collections that are partly filled, two collections with the same name.
- A 5,000-line list, an empty list, an empty or whitespace-only name, an empty library, and a media type with zero items.

## Additions after the build prompt

- **Seerr requests** (requested by the owner, 2026-10-24). Not found lines can be requested in Seerr, one at a time or all at once. Seerr address and API key live in the plugin configuration. A title is requested only when Seerr has exactly one result with the same title (and the same year if the line has one); otherwise the user picks. Media Seerr reports as requested or available is not re-requested.

- **Posters for new collections** (requested by the owner, 2026-10-24). On create only: identify the collection on TMDB with a strict name match; otherwise build a 2×2 collage from the first four member posters (like Plex). Options: TMDB then collage (default), TMDB only, off. Apply never touches existing collections' posters.
- **Posters for existing collections** (requested by the owner, 2026-10-24). One button fills in a missing poster and/or backdrop, the same way. Existing artwork is never replaced.
- **Backdrop collage** (requested by the owner, 2026-10-24). When TMDB has no backdrop, a 16:9 collage of the first four member backdrops (the owner chose a collage over rotating or single backdrops).

- **Suggest collections** (requested by the owner, 2026-10-26). On the same page, search TMDB franchises, suggest franchises the library already owns part of, load a Trakt list URL or a user's public lists, or load a curated JSON pack. Choosing one fills the collection name and the title list (with TMDB ids when the source has them) and runs Preview. It never applies by itself and never requests in Seerr until the user does that from the preview, the same as a pasted list. If a collection with that name already exists, the page asks before previewing a merge. The library is matched locally. API keys stay in the plugin configuration. A blank TMDB key falls back to Jellyfin's TMDB plugin key when one is set.

Do **not** add features beyond the build prompt and the additions listed above.
