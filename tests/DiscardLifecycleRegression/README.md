# Discard lifecycle regression harness

This dependency-free console harness compiles the actual production
`DiscardService.cs` and `InventoryItemInfo.cs` against controlled task-manager,
inventory and dialog stubs. It does not run the game or discard real items.

Run from this directory using the installed .NET 10 SDK:

```powershell
dotnet build --configfile NuGet.Config -p:NuGetAudit=false -p:MSBuildEnableWorkloadResolver=false
dotnet bin/Debug/net10.0/DiscardLifecycleRegression.dll
```

The test-local NuGet configuration clears external package sources; no NuGet
packages are required. `global.json` selects a compatible .NET 10 SDK.
If builds require access outside the workspace, use the normal approval flow.

The scenarios distinguish requesting a discard from observing its completion.
They cover confirmation and inventory timeouts, missing inventory data,
native-request failures, cancellation, duplicate commands, filtered-empty work,
and retry after failure. Reflection advances the service's private timestamps
so timeout tests finish immediately. A deliberate timestamp-field rename must
update the harness.

The harness verifies service transitions, not Dalamud IPC, plugin status-file
publication, live dialog layout or aggregate inventory relocation. Production
acknowledgment uses a closed discard dialog and disappearance from the original
slot; manually moving the item is outside this model.
