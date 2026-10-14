Run from the repository root:

```text
dotnet run --project tests/PriceFilterUiRegression/PriceFilterUiRegression.csproj -c Release
```

This offline harness compiles the production `FilterPanelComponent.cs` and `Configuration.cs` directly. Fake ImGui widgets attempt edits and refuse them inside the production disabled scopes; the tests do not implement the filter logic themselves.

Coverage includes readiness gating for all four price controls, saved values, strict comparison labels, unknown-price explanations, active counts, nonnegative threshold edits, and effective-change event behavior. It has no game or external package dependencies. The included `NuGet.Config` clears network package sources.
