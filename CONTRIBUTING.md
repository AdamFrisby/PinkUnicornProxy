# Contributing

Issues and focused pull requests are welcome. Please describe the correction form or provider protocol boundary being changed and include synthetic fixtures that contain no real credentials or private transcripts.

Before opening a pull request:

```bash
dotnet restore PinkUnicornProxy.sln --locked-mode
dotnet build PinkUnicornProxy.sln -c Release --no-restore
dotnet test PinkUnicornProxy.sln -c Release --no-build --no-restore
dotnet format PinkUnicornProxy.sln --verify-no-changes --no-restore
```

Behavioral changes should preserve these invariants:

- A no-op request is byte-for-byte identical upstream.
- Rewrites never edit system/developer instructions, tool payloads, media, code, URLs, or opaque provider fields in place.
- Opaque reasoning is removed as a complete block or item.
- Open tool/thinking transactions are deferred, never partially reconstructed.
- Unknown JSON fields and response stream events survive.
- Provider credentials and transcript bodies are not logged.
