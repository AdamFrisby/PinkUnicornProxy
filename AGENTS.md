# Repository guidance

Pink Unicorn Proxy targets .NET 10 and uses ASP.NET Core, `System.Text.Json`, and YARP direct forwarding. Keep provider schemas as raw JSON views; do not introduce vendor SDK DTOs into the forwarding path.

Run before handing off a change:

```bash
dotnet restore PinkUnicornProxy.sln --locked-mode
dotnet build PinkUnicornProxy.sln -c Release --no-restore
dotnet test PinkUnicornProxy.sln -c Release --no-build --no-restore
dotnet format PinkUnicornProxy.sln --verify-no-changes --no-restore
```

Core invariants:

- Preserve original request bytes when no rewrite occurs.
- Rescan the complete supplied transcript; do not rely on process-local conversation state.
- Never invent replacement facts.
- Never rewrite protected roles, code, URLs, media, tool payloads, IDs, or opaque fields in place.
- Remove whole stale reasoning blocks/items after an edit.
- Defer active signed-thinking/tool transactions.
- Preserve unknown JSON properties and stream responses opaquely.
- Do not log bodies or credentials.
