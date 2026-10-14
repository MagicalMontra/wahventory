# Inventory context-menu regression checks

This offline console test links the production context-menu and list-policy source files. Its small Dalamud API stubs mirror the installed API 15 menu shapes, including nullable inventory targets, base item IDs, and boxed prefix conversion.

Run from this folder:

```text
dotnet run --configuration Release
```

The checks cover target filtering, normalized identity capture across retargeting, menu structure and ordering, disabled states, policy changes between opening and clicking, blacklist precedence, reporting, error isolation, no direct discard execution, and event unsubscription. They perform no game actions and require no NuGet packages.

The production plugin must also build against the actual Dalamud SDK; these tests do not render the live context menu or verify game layout.
