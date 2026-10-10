# wahventory

Inventory management plugin for Dalamud. This fork adds an inventory refresh command.

## Install in Dalamud

Add this URL in Dalamud Settings > Experimental > Custom Plugin Repositories:

```text
https://raw.githubusercontent.com/MagicalMontra/wahventory/main/repo.json
```

Save, refresh the plugin installer, and look for **wahventory (Refresh Fork)**.
The fork uses the same plugin identity and configuration as upstream, so it
replaces the original plugin. Run one version at a time.

If upstream wahventory is already installed, disable and uninstall that plugin
first, keeping its configuration. Then install **wahventory (Refresh Fork)**
from the new repository. Do not choose **Delete plugin configuration**. Dalamud
remembers the repository used for installation, so adding the new URL alone
will not switch an existing upstream installation to this fork. You can keep
wah's original repository for its other plugins.

Release packages are available on [GitHub Releases](https://github.com/MagicalMontra/wahventory/releases).

## Commands

| Command | Action |
| --- | --- |
| `/wahventory` | Toggle the main window. |
| `/wahventory refresh` | Rescan inventory and update categories, safety assessments, and cached prices using the same refresh as the toolbar button. Works with the window closed and prints a completion message. |
| `/wahventory auto` | Start discarding items on the configured auto-discard list, subject to existing protections. |

Command arguments ignore case and surrounding spaces. Refresh completes before the command returns; it does not open the window or start a discard, desynthesis, or sale action. Existing passive-discard settings continue to run independently.

To refresh from an SND Lua macro:

```lua
yield("/wahventory refresh")
```

Upstream: [wah/wahventory](https://git.wahapp.com/wah/wahventory). The project declares AGPL-3.0-or-later in its package metadata.
