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

See the [available commands wiki](https://github.com/MagicalMontra/wahventory/wiki/Commands)
for usage and completion statuses.

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

### Offline checks

The linked-source discard lifecycle regression project is under
`tests/DiscardLifecycleRegression`. It uses fake inventory/UI/task services and
does not discard real items. Status-store regression checks are under
`tests/AutomationStatusRegression`. Live game verification is still required.

Upstream: [wah/wahventory](https://git.wahapp.com/wah/wahventory). The project declares AGPL-3.0-or-later in its package metadata.
