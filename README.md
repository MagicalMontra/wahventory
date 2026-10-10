# wahventory

Inventory management plugin for Dalamud. This fork adds an inventory refresh command.

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
