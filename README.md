# No More Crafting Noises

A Dalamud plugin that reduces mapped crafting sound effects from other nearby characters while leaving sounds classified by the game as `Player` alone.

## What it does

- Loads the sound-path list from `Data/mappings.json` (copied beside the plugin when built).
- Checks active game sound data and stops sounds on a small allowlist of crafting-specific mapped paths when their sound category is `Other`.
- Excludes the shared `sound/vfx/se_vfx_common.scd` bank because it contains unrelated effects.
- Starts filtering automatically when the plugin loads.
- Provides an in-game activity window with optional diagnostics capture and a copyable report. Open it with `/craftnoise` or from the Dalamud plugin UI.

The game exposes a sound category here, not a definitive source actor ID. The filter is therefore based on the observed `Other` category plus a crafting-specific path allowlist; please report false positives or missed sounds with a copied diagnostics report.

## Install

### Custom repository

In Dalamud, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**, then add:

`https://raw.githubusercontent.com/junegob/NoMoreCraftingNoises/master/repo.json`

Save the repository URL, open `/xlplugins`, search for **No More Crafting Noises**, and install it. This custom repository is maintained by this project, not by Dalamud; review and trust the source repository before installing. The release download becomes available after the first tagged GitHub release completes.

The plugin hooks native game functions using signatures and version-sensitive sound structures. Game updates can invalidate these; if the filter does not start, check the Dalamud log and wait for an updated build rather than assuming filtering is active.

## Diagnostics

Open the plugin controls using `/craftnoise`. Select **Start diagnostics** to collect mapped sound paths and observed sound categories. **Copy report** places the captured paths, category labels, and muted entries on the clipboard. Diagnostics capture is optional; filtering itself remains active independently. **Clear report** clears the current in-memory capture.

## License

This project is licensed under the GNU Affero General Public License, version 3.0. See [LICENSE.md](LICENSE.md).
