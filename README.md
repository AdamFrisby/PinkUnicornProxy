# Pink Unicorn Proxy

Pink Unicorn Proxy is an experimental, provider-aware HTTP reverse proxy that removes corrected assumptions from model-visible conversation history.

The motivating failure mode is familiar: an agent proposes a cause, the user rejects it, and the rejected cause remains salient because both the assumption and its negation stay in context. Pink Unicorn Proxy derives a cleaner outbound transcript in which the rejected claim is removed and an explicit replacement is expressed affirmatively.

It exposes ordinary OpenAI- and Anthropic-compatible base URLs. There is no TLS interception and no client-selectable upstream:

```text
client ──► /openai/*    ──► configured OpenAI origin
       └─► /anthropic/* ──► configured Anthropic origin
                  │
                  └─ rewrite supported JSON generation requests
```

## Status

This is a conservative v0.1 implementation. It uses high-confidence deterministic patterns and an explicit correction syntax. Ambiguous prose passes through unchanged; protocol states that cannot be rewritten safely fail closed or are reported explicitly.

## Quick start

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run --project src/PinkUnicornProxy --urls http://127.0.0.1:5080
```

Point clients at these base URLs:

| Provider | Local base URL | Default upstream |
|---|---|---|
| OpenAI | `http://127.0.0.1:5080/openai/v1` | `https://api.openai.com` |
| Anthropic | `http://127.0.0.1:5080/anthropic` | `https://api.anthropic.com` |

For example, an OpenAI-compatible request remains otherwise ordinary:

```bash
curl http://127.0.0.1:5080/openai/v1/chat/completions \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "your-model",
    "messages": [
      {"role":"assistant","content":"The cause is DNS."},
      {"role":"user","content":"No, it is not DNS. It is an expired certificate."}
    ]
  }'
```

The upstream sees the unrelated history, `It is an expired certificate.`, and no model-visible occurrence of the rejected DNS claim in editable conversation text.

## Supported request shapes

- OpenAI Chat Completions: `POST /openai/v1/chat/completions`
- OpenAI Responses with client-managed input history: `POST /openai/v1/responses`
- OpenAI Responses compaction: `POST /openai/v1/responses/compact`
- Anthropic Messages: `POST /anthropic/v1/messages`
- Anthropic token counting: `POST /anthropic/v1/messages/count_tokens`
- Every other provider path is denied by default. Set `AllowOtherPaths` only when this instance should relay the rest of the configured provider origin.

Provider response bodies, including SSE streams, are forwarded opaquely. Requests with no detected correction retain their exact original bytes.

## Correction forms

The natural-language detector intentionally recognizes a small set of complete, high-confidence forms:

```text
No, it is not DNS. It is the certificate.
No, it isn't the cache.
No, not the database — it is the connection pool.
Correction: The certificate expired yesterday.
```

For deterministic agent integrations, use explicit markers:

```text
[[forget: DNS]] [[truth: The TLS certificate has expired]]
```

The markers are consumed by the proxy and are never sent upstream. A rejection with no replacement becomes the neutral positive instruction `Reassess the issue using the remaining evidence.` The proxy never invents a replacement cause.

Each request is derived from scratch. The proxy rescans every user correction in the supplied transcript, so the behavior does not depend on an in-memory session and still works when a client resends its original history on every turn.

## What gets edited

- Exact rejected claims are removed from earlier user/assistant prose and from later assistant prose that relapsed into the same claim.
- A deictic correction such as “No, it is not that” removes the immediately preceding plain-text assistant turn.
- Correction turns become affirmative replacement facts or the neutral reassessment instruction.
- System and developer instructions, tool inputs/results, code fences, inline code, URLs, media, IDs, and unknown JSON fields are not rewritten.
- The implementation does not try to remove ordinary grammatical negatives. `Do not delete production` is not a correction and must remain intact.

See [the design notes](docs/design.md) for the full algorithm and trust boundaries.

## Opaque reasoning and tool safety

Rewriting visible text is insufficient when a provider also receives opaque reasoning derived from the old transcript.

- Anthropic: after a completed-turn rewrite, complete `thinking` and `redacted_thinking` blocks are removed, including their `signature` or encrypted `data`. Individual fields are never altered. An active thinking/tool turn is immutable, so the proxy returns `409` until that transaction is completed or abandoned.
- OpenAI Responses: `reasoning` and encrypted `compaction` items are removed after a rewrite.
- OpenAI provider-held state: history behind `previous_response_id` or `conversation` cannot be selectively edited by an HTTP proxy. The default policy returns `409`. `FreshStart` explicitly drops those references, while `PassThrough` forwards unchanged with a diagnostic header.
- Tool call/result structures are treated as protocol atoms and are never partially deleted.

These rules follow the providers’ current requirements for [Anthropic extended thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking), [Anthropic tool calls](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls), and [OpenAI manually managed conversation state](https://developers.openai.com/api/docs/guides/conversation-state).

## Configuration

Configuration is under `PinkUnicorn` in `appsettings.json`; standard ASP.NET Core environment-variable mapping works with double underscores.

```json
{
  "PinkUnicorn": {
    "Mode": "Rewrite",
    "MaximumRequestBodyBytes": 4194304,
    "MaximumCorrectionTextCharacters": 16384,
    "MaximumCorrectionsPerRequest": 32,
    "MaximumConcurrentRequests": 32,
    "StatefulResponsesPolicy": "Reject",
    "AllowOtherPaths": false,
    "AllowInsecureUpstreams": false,
    "AllowPrivateUpstreams": false,
    "ActivityTimeout": "00:10:00",
    "ConnectTimeout": "00:00:15",
    "Upstreams": {
      "OpenAI": "https://api.openai.com",
      "Anthropic": "https://api.anthropic.com"
    }
  }
}
```

For a remotely reachable deployment, set a random `PinkUnicorn__AccessToken` environment variable of at least 16 characters and send it in `X-Pink-Unicorn-Key`. The proxy consumes that header and never forwards it. Keep `AllowInsecureUpstreams` and `AllowPrivateUpstreams` disabled unless deliberately targeting a trusted local backend; both flags are required for a literal loopback HTTP origin.

Modes:

- `Rewrite`: apply safe rewrites.
- `Audit`: detect corrections and set response diagnostics without modifying the request.
- `Off`: forward without parsing supported bodies.

Responses include `X-Pink-Unicorn-Result`, with values such as `unchanged`, `rewritten`, `audit-match`, `deferred-open-tool-turn`, `protected-content-conflict`, or `unsupported-stateful-context`. Rewrites also expose non-sensitive correction and opaque-block counts. Encoded, non-JSON, or body-signed generation requests fail closed in `Rewrite` mode because changing or bypassing those bytes would break the guarantee. Conversation text and credentials are never logged by the application.

## Container

```bash
docker build -t pink-unicorn-proxy .
docker run --rm -p 127.0.0.1:5080:8080 pink-unicorn-proxy
```

## Development

```bash
dotnet restore PinkUnicornProxy.sln --locked-mode
dotnet build PinkUnicornProxy.sln -c Release --no-restore
dotnet test PinkUnicornProxy.sln -c Release --no-build --no-restore
dotnet format PinkUnicornProxy.sln --verify-no-changes --no-restore
```

The test suite covers deterministic rewrites, repeated-history behavior, provider opaque blocks, tool-turn deferral, byte-exact no-ops, fixed-origin routing, header forwarding, body limits, and opaque SSE responses.

## Fundamental limitation

The proxy can remove only context supplied through it. It cannot erase provider-held conversations unless starting a new one, nor can it reach an agent framework’s external memory, summaries, vector store, or tool-side state. Applications that need a hard deletion guarantee must make all relevant state explicit and replaceable.

Licensed under the [MIT License](LICENSE).
