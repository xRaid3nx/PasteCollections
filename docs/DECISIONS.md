# Decisions and deviations from the build prompt

Where the real Jellyfin 12.2.0 packages or this machine differed from the prompt, the real thing won. Each entry says what was decided and why.

## Environment

- **Plugin folder is `D:\Apps\Jellyfin\Data\plugins`**, not `C:\ProgramData\Jellyfin\Server\plugins`. This server runs `D:\Apps\Jellyfin\jellyfin.exe` with its data directory in `D:\Apps\Jellyfin\Data`. `deploy-dev.ps1` defaults to that path and accepts `-PluginsDir`.
- **The NuGet cache was already on D:** (`D:\Banaat\NuGet\packages`), so no change was needed.
- **`scripts/smoke-test.ps1` was added** (not in the prompt). It starts a throwaway Jellyfin 12.2 from the installed binaries, with its own data folder in `artifacts\sandbox`, port 8196, fake media files, and no internet metadata. It installs the plugin and exercises the real API end to end, including the admin-only checks. It never touches the real server.

## Build and packaging

- **`JellyfinVersion` MSBuild property** (default `12.2.0`) pins all three Jellyfin packages, so building for another server is `-p:JellyfinVersion=x.y.z` and CI can run a matrix.
- **`targetAbi` 12.2.0.0** acts as a minimum: Jellyfin refuses older servers and loads the plugin on newer ones, and then binary compatibility decides. That is why the version table in the README matters.
- **The manifest only lists the build for the newest Jellyfin version** in the matrix. Jellyfin picks a version by `targetAbi`, so one entry per plugin version is enough today. More can be added if older servers need to be supported.
- **Releases are cut locally with `scripts/publish-release.ps1`**, not by a tag-triggered workflow. The prompt's workflow committed `manifest.json` on GitHub, which would make GitHub newer than the local copy and break the "local is the source of truth" rule. The script updates `manifest.json` locally, then pushes it. `release.yaml` is kept as a manual fallback that only rebuilds zips for an existing tag.
- **CI is gated with `if: !github.event.repository.private`**. Adding a Jellyfin version is a one-line change to `JELLYFIN_VERSIONS`.
- **The repository is public** (made public at the owner's request on 2026-10-23), because Jellyfin installs plugins from a public `manifest.json` URL.

## Plugin surface

- **Page `Name` is `PasteCollections`** (used in the URL `configurationpage?name=PasteCollections`), and `DisplayName` is "Paste Collections" (shown in the sidebar). `EnableInMainMenu` places it in the dashboard sidebar.
- **Presets and last-used options use the standard plugin configuration API** (`ApiClient.getPluginConfiguration` / `updatePluginConfiguration`), not a custom endpoint, so they're stored and backed up like any other plugin setting.
- **Admin policy is `RequiresElevation`**, which is still the policy name in 12.2. It's verified by the smoke test (anonymous → 401, non-admin → 403).

## Jellyfin API behaviour

- **`AddToCollectionAsync` throws if any item id doesn't exist**, so Apply re-validates every id against the library first. Unknown ids are reported as failed instead of aborting the batch.
- **Collection members are read from `LinkedChildren`** (populated on BoxSet queries in 12.2), with `GetLinkedChildren()` as a fallback.
- **The catalog is read with `DtoOptions` stored-columns-only** in one query. Extras, owned items, and alternate versions are excluded. These instances are never saved.

## Matching

- **Library items with no year, when the list gives a year, become "Choose one"** (not Matched and not Different year), with a note. The year can't be confirmed, so the user decides.
- **Fuzzy matching uses normalised Levenshtein ≥ 0.90** on the loose key. For short titles that's effectively an exact match, which avoids false positives like *Up* vs *Us*. Fuzzy hits must also be within ±1 year when a year is given, and are never ticked by default.
- **Only `ForcedSortName`** is used as a sort-name alias. Jellyfin's computed sort name adds nothing over the normalised title.
- **Known limit: `101 Dalmatians` vs *One Hundred and One Dalmatians*.** Converting numerals to words in general would create more false matches than it fixes. It's documented in the README.

## Seerr

- **The browser never talks to Seerr.** The page calls the plugin's admin-only endpoints and the Jellyfin server calls Seerr, so the address must be reachable from the Jellyfin host and there are no CORS issues.
- **The API key is stored in the plugin configuration** next to the presets. It is visible to Jellyfin administrators (who could already read every plugin setting) and is never logged.
- **One Seerr instance, connected to Jellyfin** (the owner moved Seerr off Plex for this). With Seerr on a different media server, its "available" state wouldn't match this library.
- **Matching reuses the library title keys** (strict or loose) plus an exact year when the line has one. Anything else is a choice for the user, the same rule as fuzzy matches in Preview.
- **Requests use Seerr's defaults**: no 4K, default server and profile, all seasons for shows. Status 4/5 (partly/fully available) and 2/3 (pending/processing) are reported instead of requested; HTTP 409 from Seerr counts as already requested.
- **Bulk requests run sequentially in chunks of 10** from the page so progress is visible. If Seerr can't be reached, the rest of the chunk fails at once instead of waiting for each timeout (15 s).

## Posters for new collections

- **TMDB matching is strict**: the TMDB collection name must equal the typed name (title keys), with or without a trailing "Collection". Anything looser would put an unrelated franchise's poster on a custom collection. Jellyfin's own box set provider may still pick a poster during its background refresh; if a poster already exists, no collage is made.
- **Identify uses Jellyfin's provider manager** (`GetRemoteSearchResults<BoxSet, BoxSetInfo>` then `RefreshFullItem` with the search result and `ReplaceAllImages`), the same path as the Identify dialog. Metadata isn't replaced, so the typed name stays.
- **The collage uses Jellyfin's image encoder** (`IImageEncoder.CreateImageCollage`) at 600x900. Jellyfin 12.2 only has square and thumb collage builders; poster-shaped output falls back to the 2x2 square builder, which fits 2:3 posters into 2:3 cells. One poster is used as-is; two or three are repeated by Jellyfin to fill the grid. It's saved with `IProviderManager.SaveImage`.
- **Backdrop collages are drawn by the plugin with SkiaSharp**, because Jellyfin's collage builder only makes square and poster shapes (its 16:9 builder is a single darkened image with a library name). The plugin compiles against the SkiaSharp version that ships with the targeted Jellyfin and uses the server's copy at runtime; the release zip contains only the plugin DLL. Tiles are centre-cropped, never stretched.
- **TMDB identification fills only missing images** (`ReplaceAllImages = false`), so it never replaces artwork the user already has.
- **Lock metadata is applied after the poster** when a poster option is on, because a locked item skips the refresh that downloads TMDB artwork.
- **Poster work never fails Apply**: errors and a 60 s timeout become a message in the report.
- **Existing collections are filled one request at a time** from the page, so a long run shows progress per collection and no single HTTP call waits on dozens of TMDB lookups. Only collections without a primary image are touched; locked ones skip TMDB identification (it would rewrite their metadata) and get a collage from their own member order.
- **The first collection on a server is checked after Jellyfin's scan.** Creating it makes Jellyfin create its Collections folder and rescan the library; on Windows that scan can hold `collection.xml` open while the new collection is saved, and its members are lost (reproduced in the sandbox: all 6 dropped in 2 of 3 runs). When Apply creates the server's first collection, it waits for the `RefreshLibrary` task to go idle (up to 60 s), reads the collection back from the database and re-adds anything missing. Later collections skip this. The smoke test runs without a warm-up collection so it exercises this path.

## Suggest collections

- **Suggestions never skip Preview.** Choosing a list fills the existing page and runs Preview. Apply and Seerr stay where they were. An existing collection name asks before that preview, because Apply would merge into the largest collection of that name.
- **TMDB ids match exactly** when both the line and the library item have one, and the movie/series kind agrees. TMDB movie ids and TV ids are different sequences, so a movie id is not applied to a series. If the id is not in the library, matching falls back to the title rules.
- **The library is not uploaded.** Search sends the query. Suggest from the library asks TMDB about one movie at a time. A stored TMDB id is sent when Jellyfin has one. Otherwise the title and year are sent, and an IMDb id already stored by Jellyfin is sent to TMDB's find API first. IMDb itself is not scraped. A title is kept only when one result matches, including the year when the library item has one. Several movies with the same title are left unmatched. Answers, including "no clear movie", are cached in `paste-collections-suggest-cache.json` next to the plugin configuration. API keys are not written into that file and are not logged.
- **The TMDB key is the plugin setting when it is set, otherwise another plugin whose name looks like TMDB.** There is no login. Trakt uses a client id header only, for public user lists and list URLs. Letterboxd and IMDb are not scraped.
- **The page only offers Suggest from my library.** Search, Trakt, and pack loading stay in the API and are not shown. One owned movie is enough. Answers are remembered for 24 hours. There is no setting for either. Results are ordered by how many you own, then by how complete that is. TMDB collections are movies.
- **One library scan is bounded** (40 movies and 15 collection pages unless the call asks for fewer, never more than 80 movies) and calls are spaced so a large library does not burst TMDB. A 429 stops the batch and keeps what was saved. The page asks the user to continue.
