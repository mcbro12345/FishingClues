# Fishing Clues

A Dalamud plugin for FFXIV that gives you a better Fishing Log: spoiler-conscious, and packed with the info you actually need to find and catch fish.

## What it does

- A custom Fishing Log-style journal, organized by region and area
- Keeps caught and uncaught fish separated
- Hides fish you haven't discovered yet, so you don't get spoiled
- Shows the catch details that matter per fish: bait paths, weather, time windows, intuition requirements, hookset/tug info, snagging, lures, spearfishing data
- Native-feeling UI, built on KamiToolKit
- Can replace the default Fishing Log entry point
- Pulls fresh fishing-condition data from community sources
- Has a diagnostics command for when something looks off

## Commands

- `/fishingclues` - open the journal
- `/fishingclues settings` - open settings
- `/fishingclues diagnostics` - troubleshooting info

## Building it

You'll need the .NET 10 SDK and an internet connection (for the Dalamud.NET.Sdk NuGet restore).

```
dotnet restore src/FishingClues/FishingClues.csproj
dotnet build src/FishingClues/FishingClues.csproj -c Release
```

Targets Dalamud API level 15 via `Dalamud.NET.Sdk/15.0.0`. KamiToolKit builds from the vendored source as a project reference.

To rebuild the bundled fish data from the vendored upstream sources (needs Node.js):

```
node tools/build-fish-data.mjs
```

## Credits

Fishing Clues wouldn't exist without these open-source projects:

- [FFXIV Fish Tracker App](https://ff14fish.carbuncleplushy.com/) by icykoneko and contributors - MIT
- [KamiToolKit](https://github.com/MidoriKami/KamiToolKit) by MidoriKami - MIT
- [GatherBuddy](https://github.com/Ottermandias/GatherBuddy) by Ottermandias and contributors - Apache-2.0

Full details in `THIRD_PARTY_NOTICES.md`.

## Contributing

Issues and PRs are welcome - see `CONTRIBUTING.md`. Heads up: a good chunk of this codebase was built with AI coding tools, with me directing and testing along the way (see `AI-GENERATED-NOTICE.md`). If you're submitting AI-assisted changes too, please disclose that in your PR.

## Disclaimer

Fishing Clues is an unofficial, fan-made project. It's not affiliated with or endorsed by Square Enix or the Dalamud project. FINAL FANTASY XIV and related trademarks belong to their respective owners.

## License

MIT - see `LICENSE`. Third-party components keep their own licenses (see `THIRD_PARTY_NOTICES.md`).
