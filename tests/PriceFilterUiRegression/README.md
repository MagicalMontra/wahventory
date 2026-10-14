Run from the repository root:

```text
dotnet run --project tests/PriceFilterUiRegression/PriceFilterUiRegression.csproj -c Release
```

This offline harness compiles the production `FilterPanelComponent.cs` and `Configuration.cs` directly. Fake ImGui widgets attempt edits and refuse them inside the production disabled scopes; the tests do not implement the filter logic themselves.

Coverage includes readiness gating for all four price controls, saved values,
strict comparison labels, show-matching tooltip semantics and unknown-price
exclusion, active counts, nonnegative threshold edits, and effective-change
events. With ready, active price filters, known positive unit prices must match
one enabled condition; the production predicate itself is covered by the
separate PriceFilterRegression harness. There are no game or external package
dependencies. The included `NuGet.Config` clears network package sources.
