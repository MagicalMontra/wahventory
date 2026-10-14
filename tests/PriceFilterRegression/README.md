# Price filter regression harness

This dependency-free console harness links the production configuration,
item filter service, inventory model, and fixed item protections. Stubs only
replace the plugin configuration interface, inventory-container enum, and
native gearset lookup; no game inventory is accessed or discarded.

Run from this directory:

```powershell
dotnet build --configfile NuGet.Config -p:NuGetAudit=false -p:MSBuildEnableWorkloadResolver=false
dotnet bin/Debug/net10.0/PriceFilterRegression.dll
```

Coverage includes strict unit-price boundaries, refresh readiness, missing and
unavailable prices, combined filters, large values, existing filters, hidden
counts, and separate NQ/HQ grouping and totals. UI interaction, network pricing,
and the module's physical-stack discard checks are tested elsewhere.
