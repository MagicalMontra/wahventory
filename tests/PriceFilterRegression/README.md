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

Ready price filters show matching known positive unit prices. Above and below
use strict comparisons; enabling both shows their OR union. Unknown/unavailable
prices do not match. With no active price filter or an unfinished refresh, the
price predicates are bypassed and the six existing safety filters still apply.

Coverage includes the user's <500 and >1000 examples, equality boundaries,
cheap large stacks, refresh readiness, unknown prices, combined filters, large
values, existing protections, match counts, and separate NQ/HQ grouping/totals.
UI interaction, network pricing, and the module's physical-stack discard checks
are tested elsewhere.
