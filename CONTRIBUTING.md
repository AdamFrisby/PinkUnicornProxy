# Contributing

Issues and focused pull requests are welcome. Please describe the trigger/planner or provider protocol boundary being changed and include synthetic fixtures that contain no real credentials or private transcripts.

Before opening a pull request:

```bash
dotnet restore PinkUnicornProxy.sln --locked-mode
dotnet build PinkUnicornProxy.sln -c Release --no-restore
dotnet test PinkUnicornProxy.sln -c Release --no-build --no-restore
dotnet format PinkUnicornProxy.sln --verify-no-changes --no-restore
```

Behavioral changes should preserve these invariants:

- An unmarked request makes no auxiliary call and its entity bytes are identical upstream.
- A marked request gives the planner the complete supplied history; planner output is untrusted and constrained to stable user/assistant text IDs.
- Exact cache hits must preserve prior rewritten history bytes and must not call the planner. Prefix continuations may plan only unmatched suffix handles; prior bytes remain immutable, and concurrent ancestor/descendant misses must converge on one root.
- Durable-cache changes must retain restart persistence, first-writer-wins publication, provider/generation isolation, sliding TTL, disk-LRU limits, and fail-closed behavior on database errors.
- Increment the cache policy version when changing rewrite semantics in a way that makes previously stored authoritative histories unsafe to replay.
- Bytes outside a successfully rewritten top-level history value remain exact.
- Rewrites never edit system/developer instructions, tool payloads, media, IDs, citations, or opaque provider fields in place.
- Opaque reasoning is removed as a complete block or item.
- Open tool/thinking transactions are deferred, never partially reconstructed.
- Unknown JSON fields, end-to-end headers, ordinary provider paths, and response stream events survive.
- Primary/rewriter credentials, transcript bodies, prompts, and model plans are not logged.
