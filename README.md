# wahventory

Inventory management plugin for Dalamud. This fork adds inventory refresh and
refresh/discard completion reporting for automation.

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
| `/wahventory status` | Show the latest refresh/discard operation number, status, and confirmed stack count. |

Command arguments ignore case and surrounding spaces. Refresh completes before the command returns; it does not open the window or start a discard, desynthesis, or sale action. Existing passive-discard settings continue to run independently.

To refresh from an SND Lua macro:

```lua
yield("/wahventory refresh")
```

## Completion reporting (20.0.0.6)

Refresh reports `RUNNING`, then `DONE` after inventory enumeration, safety
assessment and categorization return. Market-price requests can continue
asynchronously; their results are not required for configured auto-discard IDs.

Discard reports `RUNNING`, confirmed progress, and exactly one terminal result:
`DONE`, `FAILED`, or `CANCELLED`. `DONE` requires the final queued item's
confirmation dialog to close and its original inventory slot to no longer hold
that item. Unavailable inventory remains unverified; timeouts fail the queue.
An empty eligible queue completes immediately. An empty configured auto-discard
list is a configuration error. Duplicate active discard requests are refused.

The plugin writes atomic status records to the current process's temporary
directory. Records contain a fresh plugin-session GUID and increasing operation
numbers, so older success signals cannot satisfy a new request. The files are
`wahventory_status_v1_<game process id>_refresh.txt` and the corresponding
`_discard.txt`. This is a result channel, not a multi-controller lock.

### SND

Save the complete files from `automation/` as SND Lua macros with matching names:
`Macro Sync`, `Wahventory Sync`, and `Clear Inventory(wah)`.
The helper macros are libraries; do not run them separately.
Replace the previous cleanup macro instead of creating a duplicate.

The cleanup worker waits on operation results instead of fixed delays. A caller
can use:

```lua
local Sync = require("Macro Sync.macro")
if not Sync.Call("Clear Inventory(wah)", 450) then return end
-- Continue only after both operations report DONE.
```

`Wahventory Sync` also exposes `Wah.Refresh(120)`, `Wah.Discard(300)` and
`Wah.Cleanup()`. These return `true, state` on verified completion or
`false, reason` on failure, cancellation, restart, supersession or timeout.
Timeouts are maximum waits and do not cancel the plugin's work. Keep one active
cleanup/controller per game process. A missing or older publisher fails before
sending commands. Upgrade the plugin before installing the new worker.

### Offline checks

The linked-source discard lifecycle regression project is under
`tests/DiscardLifecycleRegression`. It uses fake inventory/UI/task services and
does not discard real items. Status-store and SND protocol checks accompany the
release artifacts. Live game verification is still required.

Upstream: [wah/wahventory](https://git.wahapp.com/wah/wahventory). The project declares AGPL-3.0-or-later in its package metadata.
