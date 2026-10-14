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

## Inventory context menu (20.0.0.7)

Right-click an item in your inventory or Armoury Chest and open the **Wahventory**
submenu, marked with a boxed **W** at the bottom of the context menu:

- **Add to wahventory discard list** adds the item to the auto-discard list.
- **Add to wahventory blacklist** protects the item and removes it from the
  discard list and the current selection.

Blacklisted items cannot be added to the discard list or selected for discard.
List rules apply to the item type, including both normal and HQ copies. Existing
safety filters continue to apply. List edits are unavailable while a discard is
active or list sync is enabled. Manage existing entries in **Lists**.

## Market prices and filters (20.0.0.8)

The sidebar has **Market price >** and **Market price <** controls. Each hides
matching items using **gil per item**, with strict comparisons; equal prices
remain visible. Both controls and their thresholds are disabled until all price
requests for the current inventory, world and price method finish.

Unknown prices remain visible as **No data**. With either price filter enabled,
discard operations wait for pricing, and unknown prices are excluded from the
discard queue. Hidden HQ stacks cannot be discarded through a selected normal
stack of the same item.

**Settings > Market Prices > Price method** offers **Lowest current listing**
and **Average sale price (4 days)**. Both use Universalis for the selected world
and exact normal/HQ quality. The average covers the current UTC date plus the
previous three dates, is weighted by quantity sold, and rounds up to whole gil.
Hover over a price to see its world, quality, method, market upload time and fetch
time. Universalis is crowdsourced; refreshing downloads its latest snapshot and
does not perform a live market-board scan.

See the [market price guide](https://github.com/MagicalMontra/wahventory/wiki/Market-prices).

## Completion reporting (20.0.0.6 and later)

Refresh reports `RUNNING`, then `DONE` after inventory enumeration, safety
assessment and categorization return. With a price filter enabled, `DONE` also
waits for the current price refresh to settle. Otherwise market-price requests
can continue asynchronously. Failed or missing quotes remain unknown.

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

The list-policy and selection checks are in `tests/InventoryListRegression`.
`tests/InventoryContextMenuRegression` checks the submenu against fake menu,
inventory and persistence services; it does not manipulate the game inventory.

Price filtering, sidebar readiness, and the pricing cache/provider are covered by
`tests/PriceFilterRegression`, `tests/PriceFilterUiRegression`, and
`tests/PricingRegression`. These tests use synthetic responses and do not make
network requests or discard game items.

Upstream: [wah/wahventory](https://git.wahapp.com/wah/wahventory). The project declares AGPL-3.0-or-later in its package metadata.
