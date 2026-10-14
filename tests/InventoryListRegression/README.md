# Inventory list regression harness

This dependency-free console harness compiles the actual `InventoryListPolicy`,
`InventoryState`, and inventory model. Its only stub is the inventory-container
enum used by the model. It does not load the plugin, run the game, or discard
items.

Run from this directory using the installed .NET 10 SDK:

```powershell
dotnet build --configfile NuGet.Config -p:NuGetAudit=false -p:MSBuildEnableWorkloadResolver=false
dotnet bin/Debug/net10.0/InventoryListRegression.dll
```

The test-local NuGet configuration clears external package sources; no NuGet
packages are required. `global.json` selects a compatible .NET 10 SDK.

The scenarios verify blacklist precedence, explicit add results, locked/busy
operations without mutation, overlap normalization, single and category
selection, and targeted deselection across original items, filtered copies,
category copies, and duplicate stacks. Unrelated selected IDs must remain
selected. Module refresh, IPC dispatch, configuration persistence, and live UI
interaction are outside this harness.
