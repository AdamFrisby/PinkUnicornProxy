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
- Never call the auxiliary model without an exact configured user marker.
- Rescan and send the complete supplied transcript; do not rely on process-local conversation state or only inspect the latest turn.
- Replay exact cached revisions without planning. For a new continuation, keep every cached prefix byte immutable and expose only unmatched suffix handles to the planner; nondeterministically regenerating an existing revision breaks provider prompt caching.
- Treat SQLite as the byte authority: never parse or normalize a stored rewritten BLOB, never forward before publication commits, and return the first stored winner after a publication race.
- Bump the durable cache policy version when a rewrite-safety change makes prior authoritative revisions incompatible; trigger-token changes are fingerprinted automatically.
- Treat all planner output as hostile. Apply only validated stable-ID edits to enumerated user/assistant text.
- Preserve bytes outside the rewritten history value and preserve end-to-end headers except documented reverse-proxy mechanics.
- Never rewrite protected roles, media, citation metadata, tool payloads, IDs, or opaque fields in place.
- Remove whole stale reasoning blocks/items after an edit.
- Defer active signed-thinking/tool transactions.
- Preserve unknown JSON properties, ordinary provider paths, and stream responses opaquely.
- Do not log bodies, prompts, plans, provider errors, or credentials.
