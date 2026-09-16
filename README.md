# Fishing Clues

A Dalamud plugin for FFXIV that gives you a better Fishing Log: spoiler-conscious, and packed with the info you actually need to find and catch fish.

## Features

- A custom Fishing Log journal
- Keeps caught and uncaught fish separated for easy tracking
- Hides fish and fishing holes you haven't discovered yet, so you don't get spoiled
- Shows the catch details that matter per fish: bait paths, weather, time windows, intuition requirements, hookset/tug info, snagging, lures, spearfishing data
- Native UI, built on KamiToolKit
- Can replace the default Fishing Log

## Commands

- `/fishingclues` - open the journal
- `/fishingclues settings` - open settings

## Installation Instructions

1. Open the game chat and type `/xlsettings`, then click the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL into the empty box at the bottom:
   ```
   https://raw.githubusercontent.com/mcbro12345/FishingClues/main/pluginmaster.json
   ```
3. Click the **+** button to add it, then **Save and Close**.
4. Type `/xlplugins` to open the Plugin Installer, search for "Fishing Clues," and click **Install**.

That's it. Updates will show up in the Plugin Installer automatically.

## Credits

Fishing Clues wouldn't exist without these open-source projects:

- [FFXIV Fish Tracker App](https://ff14fish.carbuncleplushy.com/) by icykoneko and contributors - MIT
- [KamiToolKit](https://github.com/MidoriKami/KamiToolKit) by MidoriKami - MIT
- [GatherBuddy](https://github.com/Ottermandias/GatherBuddy) by Ottermandias and contributors - Apache-2.0
- [Teamcraft](https://ffxivteamcraft.com) by Flavien Normand and contributors - MIT

Full details in `THIRD_PARTY_NOTICES.md`.

## Contributing

Issues and PRs are welcome - see `CONTRIBUTING.md`.

## AI-assisted development

Parts of this codebase were built with AI coding tools. See `AI-GENERATED-NOTICE.md` for details.

## Disclaimer

Fishing Clues is an unofficial, fan-made project. It's not affiliated with or endorsed by Square Enix or the Dalamud project. FINAL FANTASY XIV and related trademarks belong to their respective owners.

## License

MIT - see `LICENSE`. Third-party components keep their own licenses (see `THIRD_PARTY_NOTICES.md`).
