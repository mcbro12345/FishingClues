# Fishing Clues

A Dalamud plugin for FFXIV that gives you a better Fishing Log: spoiler-conscious, and packed with the info you actually need to find and catch fish.

<details>
  <summary>
    Click to view the Fishing Clues Journal <strong>(Spoilers for some fish and locations!)</strong>
  </summary>

  <kbd>

  ![Spoiler Image](https://github.com/user-attachments/assets/8b12f65f-c947-4e65-97cf-1cdd892ff985)

  </kbd>
</details>

<details>
  <summary>
    Click to view the Fishing Clues All-Fish Search Window <strong>(Spoilers for some fish and locations!)</strong>
  </summary>

  <kbd>

  ![Spoiler Image](https://github.com/user-attachments/assets/00ef2d8f-7c14-49fd-a3fb-8420d8e6460a)

  </kbd>
</details>


## Features

- A custom Fishing Log journal.
- Keeps caught and uncaught fish separated for easy tracking.
- Hides fish and fishing holes you haven't discovered yet, so you don't get spoiled.
- Shows the catch details that matter per fish: bait, weather, time windows, hookset/tug info, lures, etc.
- Shows an uptime/downtime indicator next to fish with a time or weather window, including fish you haven't discovered yet.
- Searchable all-fish guide.
- Optionally replace the default Fishing Log (on by default upon install), or keep it and open the custom journal with its own keybind instead.
- Native UI, built on KamiToolKit.

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
