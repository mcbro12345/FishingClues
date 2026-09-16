# Fishing Clues

Fishing Clues is an open-source Dalamud plugin for **FINAL FANTASY XIV** that provides a spoiler-conscious, Fishing Log-style journal with additional information for discovering and catching fish.

> [!IMPORTANT]
> **AI-generated code:** Fishing Clues was developed with generative AI. A substantial portion of this codebase was generated through AI coding tools under project-owner direction and testing. See [AI-GENERATED-NOTICE.md](AI-GENERATED-NOTICE.md).

## What it does

- Provides a custom Fishing Log-style journal.
- Organizes fishing locations by region and area.
- Separates caught and uncaught fish.
- Keeps unrevealed fish names hidden where possible to preserve discovery.
- Shows catch-condition information such as bait paths, weather, time windows, intuition requirements, hookset/tug information, snagging, lures, and spearfishing data when available.
- Supports a native-style UI built with KamiToolKit.
- Can optionally replace the normal Fishing Log entry point.
- Can refresh fishing-condition data from upstream community data sources.
- Includes a diagnostics command for troubleshooting journal/discovery behavior.

## Current version

`3.9.0`

This repository represents the v3.9.0 source snapshot. The searchable all-fish guide, broader per-hole bait accuracy work, and bottom-dropdown animation changes discussed after this snapshot are **not** included in this version.

## Commands

- `/fishingclues` — open Fishing Clues.
- `/fishingclues settings` — open plugin settings.
- `/fishingclues diagnostics` — open diagnostic information useful for troubleshooting.

## Repository layout

```text
src/FishingClues/              Main plugin source and bundled fishing data
vendor/KamiToolKit/            Vendored KamiToolKit source (MIT)
vendor/GatherBuddy/            GatherBuddy fish-data source subset (Apache-2.0)
vendor/ff14-fish-tracker-app/  FFX|V Fish Tracker data snapshot (MIT)
tools/build-fish-data.mjs      Rebuilds FishConditions.json from vendored data
.github/workflows/             GitHub Actions build validation
```

## Building

### Requirements

- .NET 10 SDK
- Internet access for NuGet restore of `Dalamud.NET.Sdk`

From the repository root:

```bash
dotnet restore src/FishingClues/FishingClues.csproj
dotnet build src/FishingClues/FishingClues.csproj -c Release
```

The project targets Dalamud API level 15 through `Dalamud.NET.Sdk/15.0.0` and builds KamiToolKit from the vendored source as a project reference.

## Rebuilding fishing data

The checked-in `FishConditions.json` is generated from the vendored upstream data snapshots. With Node.js installed:

```bash
node tools/build-fish-data.mjs
```

The script writes the generated data to `src/FishingClues/FishConditions.json`.

## Upstream data and libraries

Fishing Clues incorporates or derives data/code from these open-source projects:

- **FFX|V Fish Tracker App** by icykoneko and contributors — MIT License.
- **KamiToolKit** by MidoriKami — MIT License.
- **GatherBuddy** by Ottermandias and contributors — Apache License 2.0.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and the license files preserved under `vendor/` for details.

## Contributing

Issues and pull requests are welcome. Please see [CONTRIBUTING.md](CONTRIBUTING.md). When submitting changes produced with AI tools, disclose that fact in the pull request so reviewers know what level of verification may be appropriate.

## Disclaimer

Fishing Clues is an unofficial third-party project. It is not affiliated with or endorsed by Square Enix, the Dalamud project, or the upstream projects whose open-source data/libraries are used here. FINAL FANTASY XIV and related names are trademarks of their respective owners.

## License

Fishing Clues is released under the [MIT License](LICENSE), copyright © 2026 mcbro.

Third-party components and data remain under their respective licenses and are not relicensed by the Fishing Clues MIT License.
