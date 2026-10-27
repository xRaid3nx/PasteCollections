# Development

## Requirements

- .NET 10 SDK
- A Jellyfin 12.2 server for manual testing (the smoke test uses the binaries in `D:\Apps\Jellyfin`)
- PowerShell 5.1+ for the scripts; GitHub CLI (`gh`) for releases

## Layout

| Path | Purpose |
|---|---|
| `src/Jellyfin.Plugin.Collections/` | The plugin. `Core/` is pure matching logic (no Jellyfin types), `Services/` talks to Jellyfin, `Api/` is the REST controller, `Configuration/` has the dashboard page and settings. |
| `src/Jellyfin.Plugin.Collections.Tests/` | xUnit tests |
| `scripts/deploy-dev.ps1` | Build and install into the local Jellyfin (run elevated if Jellyfin runs elevated) |
| `scripts/smoke-test.ps1` | End-to-end test against a throwaway Jellyfin on port 8196 |
| `scripts/package.ps1` | Build the release zip and print its MD5 |
| `scripts/update-manifest.ps1` | Add a version entry to `manifest.json` |
| `scripts/publish-release.ps1` | Package, create the GitHub release, and update `manifest.json` locally |
| `build.yaml`, `manifest.json` | Packaging metadata and the plugin repository manifest |
| `.github/workflows/` | CI (build and test) and a manual release fallback |
| `docs/` | `INTENT.md`, `DECISIONS.md`, images, and screenshots |

## Build and test

```powershell
dotnet build -c Release
dotnet test
.\scripts\smoke-test.ps1          # -Keep leaves the sandbox at http://localhost:8196 (admin / smoke-test)
.\scripts\deploy-dev.ps1          # install into the local Jellyfin and restart it
```

Build for a different Jellyfin version with `-p:JellyfinVersion=x.y.z`. Analyzers run with warnings as errors. Keep `Core/` free of Jellyfin types, and add tests for any matching change.

## REST API (admin only)

- `GET /PasteCollections/Collections`
- `POST /PasteCollections/Preview`: `{ List, CollectionName, MediaTypes, YearTolerance, Fuzzy }`
- `POST /PasteCollections/Apply`: `{ CollectionName, ItemIds, LockMetadata, Poster }` (`Poster`: `TmdbThenCollage`, `TmdbOnly` or `Off`; the response adds `Poster` and `PosterMessage`)
- `GET /PasteCollections/Posters/Missing`: collections missing a poster or a backdrop (`Id`, `Name`, `ItemCount`)
- `POST /PasteCollections/Posters/Add`: `{ CollectionId, Poster }` (`TmdbThenCollage` or `TmdbOnly`) → `{ CollectionId, Poster, Backdrop, Message }`; artwork that already exists is reported as `NotAttempted`
- `POST /PasteCollections/Seerr/Test`: `{ Url, ApiKey }` (blank values use the saved settings)
- `POST /PasteCollections/Seerr/Request`: `{ Items: [{ LineNumber, Title, Year, MediaTypes, TmdbId?, MediaType? }] }`, at most 50 per call
- `POST /PasteCollections/Preview` also accepts `KnownLines`: `[{ LineNumber, TmdbId, MediaType }]`. A TMDB id match is exact. Title matching is unchanged when the id is not in the library.
- `POST /PasteCollections/Suggest/Search`: `{ Query, MediaTypes }` → `{ Hits: [{ Source, Id, Name, Owned, Total }], RemainingLookups, Message }`
- `POST /PasteCollections/Suggest/FromLibrary`: `{ MinOwned, MaxLookups, MediaTypes }` → the same hit shape. One call looks up a bounded number of movies; `RemainingLookups` is what's left.
- `GET /PasteCollections/Suggest/Collection?source=&id=&mediaTypes=` → `{ Source, Id, Name, Items: [{ Title, Year, TmdbId, MediaType }], Message }`
- `POST /PasteCollections/Suggest/Trakt/Lists`: `{ Username }` → hits (parts not loaded yet)
- `POST /PasteCollections/Suggest/Trakt/Items`: `{ Url, MediaTypes }` → a list ready to preview
- `GET /PasteCollections/Suggest/Packs?mediaTypes=` → hits from the configured pack URL

## Releasing

Releases are cut from this PC, so the local `manifest.json` stays the source of truth.

1. Bump the version in `Directory.Build.props` and `build.yaml`, then add a `CHANGELOG.md` entry.
2. Commit.
3. Run:

   ```powershell
   .\scripts\publish-release.ps1 -Version 1.0.1.0
   ```

   The script tests and packages the plugin, creates the GitHub release `v1.0.1.0` with the zip, adds the version to `manifest.json`, then commits and pushes.

Jellyfin servers that added the repository see the update in **Dashboard → Plugins → Catalog**.
